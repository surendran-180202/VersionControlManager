#!/usr/bin/env perl
#
# Renames identifiers in C# source without touching prose or data.
#
#   perl rename-identifiers.pl <file> old=new [old=new ...]
#   perl rename-identifiers.pl --dry-run <file> old=new
#
# A plain word-boundary regex over a .cs file looks like it works and quietly
# corrupts two things:
#
#   - doc comments, where the identifier is an ordinary English word
#       "Registers a value to be masked"  ->  "Registers a strValue to be masked"
#   - string literals that happen to contain the same word
#       TryGetProperty("value", ...)      ->  TryGetProperty("strValue", ...)
#
# The second one compiles, ships, and breaks at runtime against a live API,
# which is the worst possible failure mode for a rename.
#
# This script walks each line as a small state machine and rewrites identifiers
# in code only. Interpolation holes are still code, so `$"{count} items"`
# correctly renames `count` while leaving the words `items` alone.

use strict;
use warnings;

my $dry_run = 0;

if (@ARGV && $ARGV[0] eq '--dry-run') {
    $dry_run = 1;
    shift @ARGV;
}

my $file = shift @ARGV or die "usage: rename-identifiers.pl [--dry-run] <file> old=new [old=new ...]\n";
die "no renames given\n" unless @ARGV;

my %rename;

for my $pair (@ARGV) {
    my ($old, $new) = split /=/, $pair, 2;
    die "bad rename '$pair' - expected old=new\n" unless defined $new && length $old && length $new;
    $rename{$old} = $new;
}

# Longest identifiers first, so renaming `count` never eats part of `countTotal`.
my $pattern = join '|', map { quotemeta } sort { length($b) <=> length($a) } keys %rename;

open(my $in, '<', $file) or die "cannot read $file: $!\n";
my @lines = <$in>;
close $in;

my $changes = 0;

# Rewrite identifiers in a stretch of genuine code.
#
# The lookbehind on `.` keeps a rename of local `count` from also rewriting an
# unrelated `other.count`. But `this.count` IS the member being renamed, so
# `this.` is masked out of the way first - otherwise every this-qualified field
# silently survives the rename and the build breaks on the declaration alone.
sub rewrite {
    my ($code) = @_;

    # A lambda parameter is a local like any other, so it is renamed too. Skipping
    # the `x => ...` declaration while still renaming the body's uses of `x` is the
    # one thing guaranteed to produce an undefined identifier.
    # The range operator `..` is not member access, but the guard below cannot tell
    # them apart - so `segments[..count]` would silently keep the old name and break
    # the build. Mask both `this.` and `..` before rewriting, then put them back.
    $code =~ s/\bthis\./\x00/g;
    $code =~ s/\.\./\x01/g;
    $changes += ($code =~ s/(?<![\w.])($pattern)\b/$rename{$1}/g);
    $code =~ s/\x01/../g;
    $code =~ s/\x00/this./g;

    return $code;
}

for my $line (@lines) {
    my $eol = ($line =~ s/(\r?\n)$//) ? $1 : '';
    my $out = '';
    my $i   = 0;
    my $len = length $line;

    while ($i < $len) {
        my $rest = substr($line, $i);

        # A comment runs to end of line - prose, never touched.
        if ($rest =~ m{^//}) {
            $out .= $rest;
            last;
        }

        # Start of a string literal, with any $ / @ prefixes.
        if ($rest =~ /^(\$?\@?\$?)"/) {
            my $prefix   = $1;
            my $interp   = $prefix =~ /\$/ ? 1 : 0;
            my $verbatim = $prefix =~ /\@/ ? 1 : 0;

            $out .= $prefix . '"';
            $i   += length($prefix) + 1;

            # Consume the literal, treating interpolation holes as code.
            while ($i < $len) {
                my $ch = substr($line, $i, 1);

                if (!$verbatim && $ch eq '\\') {
                    $out .= substr($line, $i, 2);
                    $i += 2;
                    next;
                }

                if ($verbatim && $ch eq '"' && substr($line, $i + 1, 1) eq '"') {
                    $out .= '""';
                    $i += 2;
                    next;
                }

                if ($ch eq '"') {
                    $out .= '"';
                    $i++;
                    last;
                }

                if ($interp && $ch eq '{') {
                    if (substr($line, $i + 1, 1) eq '{') {
                        $out .= '{{';
                        $i += 2;
                        next;
                    }

                    # Inside the hole is code: find the matching brace.
                    my $depth = 0;
                    my $start = $i;

                    while ($i < $len) {
                        my $c = substr($line, $i, 1);
                        $depth++ if $c eq '{';
                        $depth-- if $c eq '}';
                        $i++;
                        last if $depth == 0;
                    }

                    my $hole = substr($line, $start, $i - $start);
                    $hole =~ s/^\{(.*)\}$/$1/s;
                    $out .= '{' . rewrite($hole) . '}';
                    next;
                }

                $out .= $ch;
                $i++;
            }

            next;
        }

        # A run of ordinary code up to the next quote or comment.
        if ($rest =~ m{^(.*?)(?=//|\$?\@?\$?"|$)}s && length $1) {
            $out .= rewrite($1);
            $i += length $1;
            next;
        }

        $out .= substr($line, $i, 1);
        $i++;
    }

    $line = $out . $eol;
}

if ($dry_run) {
    print "would make $changes replacement(s) in $file\n";
    exit 0;
}

open(my $fh, '>', $file) or die "cannot write $file: $!\n";
print $fh @lines;
close $fh;

print "$file: $changes replacement(s)\n";
