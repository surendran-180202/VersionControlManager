---
name: csharp-cleanup
description: Apply the C# house style to a codebase - explicit types instead of var, Hungarian-style naming (strName, nCount, bFlag, liItems, UPPER_SNAKE_CASE constants), members grouped into #region blocks (Constants/Fields/Constructors/Properties/Publics/Privates), and Visual Studio formatting enforced through .editorconfig and dotnet format. Use this whenever the user asks to clean up, tidy, reorganise, restructure or reformat C# code, or to "apply the house style"; whenever they mention #region grouping, replacing var with explicit types, variable naming prefixes or renaming locals to match their type, .editorconfig, dotnet format, or inconsistent tabs/spaces in a .NET project; and when scaffolding a new C# project that should follow the house conventions. Reach for it too when reviewing C# for style consistency, or when a file mysteriously shows huge whitespace-only diffs, even if the user never names the specific rules.
---

# C# cleanup

Four phases, run in this order: **explicit types → naming → region grouping → formatting**.

The order matters. The first three need judgment and rewrite real code; the formatter is
mechanical and normalises whatever they produced. Running it first just means running it twice.
Naming follows the type pass because a prefix is chosen from the declared type, which is only
unambiguous once `var` is gone.

Do the whole pass on a clean working tree so the diff is reviewable, and verify after every
phase rather than at the end — a build break is far cheaper to locate one phase back.

## Phase 1 — explicit types instead of `var`

Every `var` becomes the type it actually is. This is not a find-and-replace: each site needs
the real type, and the compiler is the judge.

The declarations people usually miss:

| Form | Written as |
| --- | --- |
| Local | `string url = ...` |
| `out` parameter | `out Uri? uri`, `out JsonElement value`, `out int parsed` |
| `foreach` element | `foreach (string file in ...)` |
| Tuple deconstruction | `(RepoInfo repo, bool created) = await ...` |
| List pattern | `is [_, _, string rest]` |
| `using` declaration | `using JsonDocument? document = ...` |

Nullability is where this goes wrong. `Console.ReadLine()` returns `string?`, not `string`.
`Dictionary.TryGetValue` and `Uri.TryCreate` hand back nullable `out` values. A method
documented as "returns null when absent" needs `?` on the declaration. Under
`TreatWarningsAsErrors` a wrong annotation fails the build, which is the quickest feedback
available — build after this phase before moving on.

Longer type names push lines out. When a line crosses ~120 characters, wrap it rather than
leaving it long, and re-align any continuation lines that were aligned to the old shorter
declaration.

## Phase 2 — naming

Locals and parameters carry a prefix taken from their type. Constants are `UPPER_SNAKE_CASE`.
Class-level fields keep whatever they are called — the prefix rule applies inside method
bodies, not to the type's own members.

| Type | Prefix | Example |
| --- | --- | --- |
| `string` | `str` | `strName` |
| `int` | `n` | `nCount` |
| `long` | `l` | `lSize` |
| `bool` | `b` | `bIsValid` |
| `object` | `o` | `oPayload` |
| `List<T>`, `IReadOnlyList<T>`, `T[]` | `li` | `liItems` |
| A POCO class `Abc` | camelCase of the class name | `abc` |
| `const`, any type | `UPPER_SNAKE_CASE` | `SERVICE_NAME` |

Types outside this table — framework types like `HttpClient` or `JsonDocument`, enums,
`double` — keep their existing names. Extending the POCO rule to every framework type reads
well until two variables of the same type share a scope and collide, so leave them be unless
the user asks otherwise.

### Renaming safely

Use `scripts/rename-identifiers.pl`, which rewrites code while leaving prose and data alone:

```bash
perl <skill>/scripts/rename-identifiers.pl Foo.cs value=strValue count=nCount
perl <skill>/scripts/rename-identifiers.pl --dry-run Foo.cs value=strValue
```

A plain `sed`/`perl` word-boundary pass over a `.cs` file is the obvious approach and it is
wrong in two ways that look very different in severity:

- It rewrites **doc comments**, turning "Registers a value to be masked" into "Registers a
  strValue to be masked". Ugly, obvious, easily fixed.
- It rewrites **string literals**, turning `TryGetProperty("value", ...)` into
  `TryGetProperty("strValue", ...)`. That compiles, passes review, and fails at runtime
  against a live API. This is the one that matters.

The script also handles four cases that each cost a broken build otherwise:

- **`this.`-qualified members.** Guarding against renaming `other.count` also blocks
  `this.count`, which is the member being renamed. After a formatting pass adds `this.`
  everywhere, this silently misses nearly every field.
- **The range operator.** `liSegments[..count]` has a `..` before the identifier, which any
  member-access guard reads as `obj.field`.
- **Lambda parameters.** Skipping the `x => ...` declaration while renaming the body's uses of
  `x` produces an undefined identifier. Rename both or neither.
- **Named arguments at call sites.** Renaming a parameter breaks `Method(oldName: value)` in
  *other files*. After renaming a parameter, grep the solution for `oldName:` and rename there
  too — the compiler will tell you, but only after you have moved on.

### Collisions

Two variables of the same type in one scope cannot both take the plain prefixed name, and a
file-wide rename cannot see scope. When the same identifier means different things in
different methods — `created` as a `bool` in one and an `AzureRepositoryInfo` in another — a
single rename gives one of them the wrong prefix.

Rename the narrower case first, scoped to its line range, then run the file-wide pass:

```bash
sed -i '157,161 s/\bcreated\b/azureRepositoryInfoNew/g' Runner.cs
perl <skill>/scripts/rename-identifiers.pl Runner.cs created=bCreated
```

## Phase 3 — group members into regions

Every type's members are grouped into `#region` blocks in this order:

```
#region Constants
#region Fields
#region Constructors
#region Properties
#region Publics
#region Privates
```

Emit only the regions a type actually needs. Empty placeholder regions are noise — a class
with three public methods and nothing else gets one region, not six.

Region names are single plural nouns. `Publics` and `Privates`, never "Public Methods" or
"Private Methods", so every name in the codebase reads the same way.

**Move members that are in the wrong place.** This is the part that is easy to skip and is
most of the value. Wrapping markers around the existing order leaves a private helper sitting
in the middle of the public API, which is exactly the thing the grouping is meant to fix. Read
the members, decide where each belongs, then move it. In practice the strays are private
helpers that grew up next to their caller, and `Dispose` sitting at the bottom under the
private methods.

Reordering members is safe in C# with one exception worth checking: static field initialisers
run in declaration order, so if one field's initialiser reads another, keep their relative
order.

### Spacing

Region tags sit flush against the members they contain — no blank line after `#region`, none
before `#endregion`. Keep one blank line between a closing `#endregion` and the next `#region`:

```csharp
	#region Constants
	private const string SERVICE_NAME = "Azure DevOps";
	private const string API_VERSION = "7.1";
	#endregion

	#region Properties
	private string ProjectApiRoot => $"{this.CollectionUrl}/_apis/git";
	#endregion
```

Keep doc comments attached to their member — a `#region` goes *above* the `///` block, never
between the comment and what it documents.

### Doing the edit

For a file whose members are already in the right order, inserting markers is enough; `sed`
handles single-line anchors and `perl -0777 -pi -e` handles anchors that span lines (like a
`#region` that must land above a multi-line doc comment). For a file that needs members moved,
rewriting the file wholesale is more reliable than a sequence of surgical edits.

Two gotchas that will cost time otherwise:

- In perl, `s{...}{...}` breaks when the replacement contains a literal `}`. Use a different
  delimiter — `s!...!...!` — for anything inserting C# braces.
- A here-doc carrying a large C# file through the shell can be truncated mid-command. Past a
  few dozen lines, write the file with the file-writing tool instead.

`scripts/list-members.pl` prints each type's members in source order with the region each one
belongs in, which makes the regrouping plan obvious before any edit.

## Phase 4 — formatting via .editorconfig and dotnet format

Copy `assets/editorconfig.template` into the repository root as `.editorconfig`. It encodes
the whole style: tab indentation, no space after a control-flow keyword (`if(x)`), instance
members qualified with `this.`, block bodies for methods but expression bodies kept for
properties, plus the no-`var` preference from phase 1.

This file is the point of the phase. Visual Studio, Rider and `dotnet format` all read it, so
the style is enforced rather than reapplied by hand every time someone edits a file.

Apply it with the formatter — never by hand:

```bash
dotnet format whitespace YourSolution.sln
dotnet format style YourSolution.sln --severity info
```

Both invocations have traps that produce confusing failures:

- **Name the solution or project explicitly.** With a bare `dotnet format` in a folder holding
  both a `.sln` and a `.csproj`, workspace resolution throws a stack trace from
  `ParseWorkspaceOptions` rather than a readable error.
- **`--severity` accepts `info`, `warn` or `error` only.** Passing `suggestion` — the word the
  `.editorconfig` severities use — makes the command print its help text and change nothing,
  which looks a lot like a no-op success.

Both commands exit 0 on success. If a run exits 0 but nothing changed, suspect the arguments
before concluding the code was already clean.

## Verify

Run `scripts/check-style.pl` over the source files. It checks the invariants this skill is
responsible for and prints a line per violation:

```bash
perl <skill>/scripts/check-style.pl src/**/*.cs      # or a directory, which it walks
```

It reports: any `var` left, constants not in UPPER_SNAKE_CASE, locals whose prefix does not
match their type, unbalanced `#region`/`#endregion`, blank lines against a region
tag, region names outside the allowed set, regions out of canonical order, class-level members
sitting outside any region, and mixed tab/space indentation.

Then confirm behaviour is genuinely unchanged, because everything here is meant to be a
no-op at runtime:

- Build clean. With `TreatWarningsAsErrors` a warning is a failure, which is what makes the
  type annotations trustworthy.
- Run whatever tests exist.
- Exercise the entry point if it is a tool — exit codes and a smoke run catch things a build
  does not.

## When a diff looks far bigger than the change

A single file showing hundreds of changed lines while its siblings show three is the signature
of an editor reformatting on save. Visual Studio does this to whatever file is currently open,
in the background, using its own settings — which is precisely the disagreement `.editorconfig`
exists to end.

Read the diff before committing it. Distinguishing the intended change from editor churn is
worth the minute it takes:

```bash
git diff --stat                       # one file wildly out of proportion?
git diff <file> | grep -E '^[-+]' | grep -v '^[-+][-+]' | head -40
```

If the churn is unrelated formatting, restore the file and re-apply just the intended edit, so
the commit says what it means:

```bash
cp <file> /tmp/file.editor-version.cs   # keep a copy first — it may be wanted later
git checkout HEAD -- <file>
# re-apply the real change
```

Then decide with the user which style wins rather than picking silently. If the editor's
version is the one they want, the answer is to encode it in `.editorconfig` and let
`dotnet format` apply it everywhere — that is phase 3, and it settles the question permanently.

## Finishing

Commit with the repository's convention. On this user's repositories that means an ALL-CAPS
type prefix chosen by what changed: `CHANGED:` for a cleanup pass like this one, `ADDED:` for
net-new files, `DELETED:` for removals.

State in the message that behaviour is unchanged and say what was verified. A reviewer facing
a two-thousand-line whitespace diff needs to know the build is clean and the tests pass
without re-deriving it.
