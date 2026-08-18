#!/usr/bin/env perl
#
# Collapses a braced single-statement `if` into a one-line guard clause.
#
#   perl inline-guards.pl <file-or-directory> [...]
#   perl inline-guards.pl --dry-run <file>
#
#       if(strValue is null)
#       {
#           return null;
#       }
#   becomes
#       if(strValue is null) return null;
#
# Only the unambiguous case is touched, because everything else is a footgun:
#
#   - the body must be exactly one statement, on one line
#   - no `else` may follow, since the brace is what binds it
#   - nothing is collapsed past --max-width, or the guard stops being readable
#   - `if` blocks containing another `if` are skipped outright
#
# Dropping braces is a real trade-off. It reads well for early-return guards and
# badly for anything else, which is why the conditions above are narrow: a guard
# clause that grows a second statement later must get its braces back, and the
# reviewer needs to see that happen.

use strict;
use warnings;

my $dry_run   = 0;
my $max_width = 160;
my @paths;

while (@ARGV) {
    my $arg = shift @ARGV;

    if    ($arg eq '--dry-run')   { $dry_run = 1 }
    elsif ($arg eq '--max-width') { $max_width = shift @ARGV }
    else                          { push @paths, $arg }
}

@paths = ('.') unless @paths;

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

my $total = 0;

for my $file (collect(@paths)) {
    open(my $fh, '<', $file) or next;
    local $/;
    my $source = <$fh>;
    close $fh;

    my $count = 0;

    # if(<cond>) \n <indent>{ \n <indent+1><statement>; \n <indent>} \n   not followed by else
    $source =~ s{
        (\r?\n)(\t+)if\((.+?)\)\r?\n
        \2\{\r?\n
        \2\t((?!if\b)[^\n]+?;)[ \t]*(?:(//[^\n]*))?\r?\n
        \2\}\r?\n
        (?!\2else)
    }{
        my ($nl, $indent, $cond, $stmt, $comment) = ($1, $2, $3, $4, $5);
        my $line = "$indent" . "if($cond) $stmt" . (defined $comment ? "   $comment" : "");

        # Tabs read as one column here but display wider; be conservative.
        if (length($line) + length($indent) * 3 <= $max_width && $cond !~ /\bif\b/) {
            $count++;
            "$nl$line$nl";
        }
        else {
            $&;
        }
    }gexs;

    next unless $count;

    $total += $count;
    print "  $file: $count guard(s)\n";

    next if $dry_run;

    open(my $out, '>', $file) or die "cannot write $file: $!\n";
    print $out $source;
    close $out;
}

print $dry_run ? "would inline $total guard(s)\n" : "inlined $total guard(s)\n";
