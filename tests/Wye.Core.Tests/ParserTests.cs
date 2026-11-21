using Wye.Core.Models;
using Wye.Core.Parsing;

namespace Wye.Core.Tests;

public class StatusParserTests
{
    [Fact]
    public void Parses_branch_header_and_all_entry_types()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/file with spaces.cs",
            "1 A. N... 000000 100644 100644 0000 cccc added.txt",
            "2 R. N... 100644 100644 100644 dddd dddd R100 new name.txt",
            "old name.txt",
            "u UU N... 100644 100644 100644 100644 e1 e2 e3 conflict.txt",
            "? untracked.md",
            "! ignored.log",
            "");

        var result = StatusParser.Parse(output);

        Assert.Equal("main", result.Branch.Head);
        Assert.Equal("origin/main", result.Branch.Upstream);
        Assert.Equal(2, result.Branch.Ahead);
        Assert.Equal(1, result.Branch.Behind);
        Assert.False(result.Branch.IsUnborn);

        Assert.Collection(result.Files,
            f =>
            {
                Assert.Equal("src/file with spaces.cs", f.Path);
                Assert.Equal(ChangeKind.Modified, f.Kind);
                Assert.False(f.HasStagedChanges);
                Assert.True(f.HasUnstagedChanges);
            },
            f =>
            {
                Assert.Equal(ChangeKind.Added, f.Kind);
                Assert.True(f.HasStagedChanges);
                Assert.False(f.HasUnstagedChanges);
            },
            f =>
            {
                Assert.Equal(ChangeKind.Renamed, f.Kind);
                Assert.Equal("new name.txt", f.Path);
                Assert.Equal("old name.txt", f.OldPath);
            },
            f => Assert.Equal(ChangeKind.Conflicted, f.Kind),
            f => Assert.Equal(ChangeKind.Untracked, f.Kind));
    }

    [Fact]
    public void Initial_and_detached_heads()
    {
        var unborn = StatusParser.Parse("# branch.oid (initial)\0# branch.head main\0");
        Assert.True(unborn.Branch.IsUnborn);

        var detached = StatusParser.Parse("# branch.oid abc\0# branch.head (detached)\0");
        Assert.True(detached.Branch.IsDetached);
    }
}

public class LogParserTests
{
    [Fact]
    public void Parses_multiple_commits_with_multiline_body_and_refs()
    {
        static string Rec(params string[] f) => string.Join('\0', f) + '\0';
        var output =
            Rec("a1", "a", "Second", "Line 1\nLine 2\n", "Ana", "ana@x.dev", "1700000000",
                "Ana", "ana@x.dev", "1700000001", "b2", "HEAD -> main, origin/main") +
            Rec("b2", "b", "First", "", "Bob", "bob@x.dev", "1600000000",
                "Bob", "bob@x.dev", "1600000000", "", "");

        var commits = LogParser.Parse(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal("Second", commits[0].Summary);
        Assert.Equal("Line 1\nLine 2", commits[0].Body);
        Assert.Equal(["b2"], commits[0].Parents);
        Assert.Equal(["HEAD -> main", "origin/main"], commits[0].Refs);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), commits[0].Author.Date);
        Assert.Empty(commits[1].Parents);
    }
}

public class DiffParserTests
{
    private const string Diff = """
        diff --git a/f.txt b/f.txt
        index 1111111..2222222 100644
        --- a/f.txt
        +++ b/f.txt
        @@ -1,4 +1,4 @@
         one
        -two
        +TWO
         three
        -four
        +FOUR
        \ No newline at end of file

        """;

    [Fact]
    public void Parses_hunk_and_line_numbers()
    {
        var d = DiffParser.Parse(Diff.ReplaceLineEndings("\n"));

        Assert.Equal("f.txt", d.OldPath);
        Assert.Equal("f.txt", d.NewPath);
        var hunk = Assert.Single(d.Hunks);
        Assert.Equal((1, 4, 1, 4), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));

        var del = d.Lines.First(l => l.Type == DiffLineType.Delete);
        Assert.Equal("two", del.Text);
        Assert.Equal(2, del.OldLineNumber);
        var add = d.Lines.First(l => l.Type == DiffLineType.Add);
        Assert.Equal(2, add.NewLineNumber);
        Assert.Equal(DiffLineType.NoNewlineMarker, d.Lines[^1].Type);
    }

    [Fact]
    public void Detects_binary()
    {
        var d = DiffParser.Parse("diff --git a/x.png b/x.png\nindex 1..2 100644\nBinary files a/x.png and b/x.png differ\n");
        Assert.True(d.IsBinary);
        Assert.Empty(d.Hunks);
    }

    [Fact]
    public void New_file_has_null_old_path()
    {
        var d = DiffParser.Parse("diff --git a/n b/n\nnew file mode 100644\n--- /dev/null\n+++ b/n\n@@ -0,0 +1,2 @@\n+a\n+b\n");
        Assert.Null(d.OldPath);
        Assert.Equal("n", d.NewPath);
        Assert.Equal(2, d.Lines.Count(l => l.Type == DiffLineType.Add));
    }
}

public class PatchBuilderTests
{
    private static FileDiff Sample() => DiffParser.Parse(
        "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n" +
        "@@ -1,4 +1,4 @@\n one\n-two\n+TWO\n three\n-four\n+FOUR\n");

    [Fact]
    public void Forward_keeps_selected_changes_and_turns_unselected_deletes_into_context()
    {
        var d = Sample();
        var twoDel = d.Lines.First(l => l.Text == "two").Index;
        var twoAdd = d.Lines.First(l => l.Text == "TWO").Index;

        var patch = PatchBuilder.Build(d, new HashSet<int> { twoDel, twoAdd }, PatchDirection.Forward);

        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n" +
            "@@ -1,4 +1,4 @@\n one\n-two\n+TWO\n three\n four\n",
            patch);
    }

    [Fact]
    public void Forward_drops_unselected_adds()
    {
        var d = Sample();
        var fourDel = d.Lines.First(l => l.Text == "four").Index;

        var patch = PatchBuilder.Build(d, new HashSet<int> { fourDel }, PatchDirection.Forward);

        Assert.EndsWith("@@ -1,4 +1,3 @@\n one\n two\n three\n-four\n", patch);
    }

    [Fact]
    public void Reverse_turns_unselected_adds_into_context()
    {
        var d = Sample();
        var fourAdd = d.Lines.First(l => l.Text == "FOUR").Index;

        var patch = PatchBuilder.Build(d, new HashSet<int> { fourAdd }, PatchDirection.Reverse);

        Assert.EndsWith("@@ -1,3 +1,4 @@\n one\n TWO\n three\n+FOUR\n", patch);
    }

    [Fact]
    public void Nothing_selected_returns_null()
    {
        Assert.Null(PatchBuilder.Build(Sample(), new HashSet<int>(), PatchDirection.Forward));
    }
}
