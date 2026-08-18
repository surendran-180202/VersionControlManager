#!/usr/bin/env perl
#
# Lists each type's members in source order with the region each one belongs in,
# so the regrouping plan is visible before any edit is made.
#
#   perl list-members.pl <file-or-directory> [...]     (defaults to ".")
#
# A "!" in the left column marks a member that is not in canonical order relative
# to the one before it - those are the members that actually need moving, as
# opposed to the ones that only need a marker inserted above them.

use strict;
use warnings;

my @ALLOWED = qw(Constants Fields Constructors Properties Publics Privates);
my %ORDER;
@ORDER{@ALLOWED} = (0 .. $#ALLOWED);

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

# Work out which region a member declaration belongs in.
sub classify {
    my ($line, $type_name) = @_;

    return 'Constants' if $line =~ /\bconst\b/;

    # A constructor names the type and takes an argument list, with no return type.
    return 'Constructors'
        if defined $type_name && $line =~ /\b\Q$type_name\E\s*\(/ && $line !~ /\b(?:void|Task|string|int|bool|long|double)\b/;

    my $is_private = $line =~ /\b(?:private|protected)\b/;

    # A property has no parameter list before its body or arrow.
    if ($line !~ /\(/ && ($line =~ /\{\s*get\b/ || $line =~ /=>/ || $line =~ /\{\s*$/)) {
        return 'Properties';
    }

    # A field: no parens, no body, ends in ; or an initialiser.
    if ($line !~ /\(/ && $line =~ /;\s*$/) {
        return 'Fields';
    }

    return $is_private ? 'Privates' : 'Publics';
}

my @files = collect(@ARGV ? @ARGV : ('.'));

for my $file (@files) {
    open(my $fh, '<', $file) or next;
    my @lines = <$fh>;
    close $fh;
    chomp @lines;

    print "\n=== $file\n";

    my $type_name  = undef;
    my $last_order = -1;

    for my $i (0 .. $#lines) {
        my $line = $lines[$i];
        $line =~ s/\r$//;
        next if $line =~ m{^\s*//};

        # Top-level type declaration.
        if ($line =~ /^\S/ && $line =~ /\b(?:class|record|struct|interface)\s+(\w+)/) {
            $type_name  = $1;
            $last_order = -1;
            print "\n  type $type_name\n";
            next;
        }

        # A class-level member sits at exactly one indent level.
        next unless $line =~ /^(?:\t|[ ]{4})(?:public|private|protected|internal)\s/;

        my $region = classify($line, $type_name);
        my $order  = $ORDER{$region};
        my $flag   = (defined $order && $order < $last_order) ? '!' : ' ';
        $last_order = $order if defined $order && $order > $last_order;

        my $text = $line;
        $text =~ s/^\s+//;
        $text = substr($text, 0, 72);

        printf "  %s %-12s %s\n", $flag, $region, $text;
    }
}

print "\n(\"!\" marks a member that is out of canonical order and needs moving.)\n";
