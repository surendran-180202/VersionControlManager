#!/usr/bin/env perl
#
# Verifies the C# house-style invariants after a cleanup pass.
#
#   perl check-style.pl <file-or-directory> [...]     (defaults to ".")
#
# Prints one line per violation and exits 1 if any were found, 0 when clean,
# so it can be dropped into a build or a pre-commit hook.
#
# Checks:
#   - no `var` left in the source
#   - #region / #endregion balance
#   - no blank line directly after #region or directly before #endregion
#   - region names limited to the canonical set
#   - regions appear in canonical order within each type
#   - no class-level member sitting outside a region
#   - no mixed tab/space indentation within a file

use strict;
use warnings;

my @ALLOWED = qw(Constants Fields Constructors Properties Publics Privates);
my %ORDER;
@ORDER{@ALLOWED} = (0 .. $#ALLOWED);

my $violations = 0;

sub report {
    my ($file, $line, $message) = @_;
    $violations++;
    print $line ? "$file:$line  $message\n" : "$file  $message\n";
}

# Collect .cs files, skipping build output.
sub collect {
    my @out;
    for my $path (@_) {
        if (-d $path) {
            opendir(my $dh, $path) or next;
            my @entries = grep { $_ ne '.' && $_ ne '..' } readdir($dh);
            closedir $dh;
            for my $entry (@entries) {
                next if $entry eq 'bin' || $entry eq 'obj' || $entry eq '.git' || $entry eq '.vs';
                push @out, collect("$path/$entry");
            }
        }
        elsif ($path =~ /\.cs$/i) {
            push @out, $path;
        }
    }
    return @out;
}

my @files = collect(@ARGV ? @ARGV : ('.'));

unless (@files) {
    print "No .cs files found.\n";
    exit 0;
}

for my $file (@files) {
    open(my $fh, '<', $file) or do { warn "cannot read $file\n"; next; };
    my @lines = <$fh>;
    close $fh;
    chomp @lines;

    my $depth        = 0;
    my $last_order   = -1;
    my $open_regions = 0;
    my $close_regions = 0;
    my $tab_lines    = 0;
    my $space_lines  = 0;
    my $in_raw       = 0;

    for my $i (0 .. $#lines) {
        my $line = $lines[$i];
        $line =~ s/\r$//;
        my $n = $i + 1;

        # Raw string literals (""" ... """) carry their own indentation as content --
        # a help screen or embedded template is not code and must not be judged as such.
        my $fences = () = $line =~ /"""/g;

        if ($in_raw) {
            $in_raw = 0 if $fences % 2;
            next;
        }

        if ($fences % 2) {
            $in_raw = 1;
            next;
        }

        $tab_lines++   if $line =~ /^\t/;
        $space_lines++ if $line =~ /^ {4}/;

        my $is_comment = $line =~ m{^\s*//};

        if (!$is_comment && $line =~ /(?:^|[^A-Za-z0-9_.])var\s/) {
            report($file, $n, "`var` used - write the explicit type");
        }

        # An anonymous object takes its property names from the variable, so a renamed
        # local silently rewrites the JSON a caller sends. This compiles and only fails
        # against the live service, so shorthand carrying a type prefix is always a bug.
        if (!$is_comment && $line =~ /new\s*\{/) {
            # Scan only what is between the braces. Method arguments on the same line
            # are not properties, and flagging them buries the real finding.
            my $scan = $line;

            while ($scan =~ /new\s*\{([^{}]*)\}/) {
                my $body = $1;
                $scan =~ s/new\s*\{[^{}]*\}/\x02/;

                # A shorthand property is a bare identifier between commas; anything
                # with an `=` has been named explicitly and is safe.
                while ($body =~ /(?:^|,)\s*((?:str|li|[nlbo])[A-Z]\w*)\s*(?=,|$)/g) {
                    report($file, $n,
                        "anonymous object uses shorthand '$1' - the JSON property will be " .
                        "named '$1'; name it explicitly instead");
                }
            }
        }

        # Constants are UPPER_SNAKE_CASE regardless of visibility.
        if (!$is_comment && $line =~ /\bconst\s+[\w<>?\[\]]+\s+(\w+)\s*=/) {
            my $const_name = $1;
            report($file, $n, "constant '$const_name' should be UPPER_SNAKE_CASE")
                unless $const_name =~ /^[A-Z][A-Z0-9_]*$/;
        }

        # Locals and parameters carry a type prefix. Class-level fields are exempt,
        # so only look inside method bodies - two or more indent levels in.
        if (!$is_comment && $line =~ /^(?:\t{2,}|(?: {8,}))([\w<>?\[\],\s]+?)\s+([a-z]\w*)\s*=[^=]/) {
            my ($type, $var) = ($1, $2);
            $type =~ s/^.*\b(?:out|ref|in)\s+//;

            my $want;
            if    ($type =~ /^(?:List|IReadOnlyList|IList|IEnumerable|ICollection)</ || $type =~ /\[\]$/) { $want = 'li' }
            elsif ($type =~ /^string\??$/)  { $want = 'str' }
            elsif ($type =~ /^int\??$/)     { $want = 'n' }
            elsif ($type =~ /^long\??$/)    { $want = 'l' }
            elsif ($type =~ /^bool\??$/)    { $want = 'b' }
            elsif ($type =~ /^object\??$/)  { $want = 'o' }

            if (defined $want) {
                my $ok = $var =~ /^\Q$want\E[A-Z]/;
                report($file, $n, "local '$var' of type $type should start with '$want'") unless $ok;
            }
        }

        # A top-level type declaration restarts the region ordering.
        if ($line =~ /^\S/ && $line =~ /\b(?:class|record|struct|interface)\b/ && !$is_comment) {
            $last_order = -1;
        }

        if ($line =~ /^\s*#region\s*(.*)$/) {
            my $name = $1;
            $name =~ s/\s+$//;
            $open_regions++;
            $depth++;

            if (!exists $ORDER{$name}) {
                report($file, $n, "region '$name' is not one of: @ALLOWED");
            }
            else {
                report($file, $n, "region '$name' is out of canonical order")
                    if $ORDER{$name} <= $last_order;
                $last_order = $ORDER{$name};
            }

            report($file, $n + 1, "blank line after #region - tags sit flush against members")
                if $i < $#lines && $lines[$i + 1] =~ /^\s*$/;
        }
        elsif ($line =~ /^\s*#endregion/) {
            $close_regions++;
            $depth-- if $depth > 0;

            report($file, $n - 1, "blank line before #endregion - tags sit flush against members")
                if $i > 0 && $lines[$i - 1] =~ /^\s*$/;
        }
        elsif ($depth == 0 && $line =~ /^(?:\t|[ ]{4})(?:public|private|protected|internal)\s/) {
            report($file, $n, "member sits outside any #region");
        }
    }

    report($file, 0, "#region/#endregion unbalanced ($open_regions open, $close_regions closed)")
        if $open_regions != $close_regions;

    report($file, 0, "mixed indentation: $tab_lines tab-indented and $space_lines space-indented lines")
        if $tab_lines && $space_lines;
}

my $count = scalar @files;

if ($violations) {
    print "\n$violations violation(s) across $count file(s).\n";
    exit 1;
}

print "Clean - $count file(s) match the house style.\n";
exit 0;
