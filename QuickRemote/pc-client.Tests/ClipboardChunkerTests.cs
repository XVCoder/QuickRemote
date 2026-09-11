using QuickRemote.PCClient.Interop;
using Xunit;

namespace QuickRemote.PCClient.Tests;

/// <summary>与 Android 端 ClipboardChunkerTest 一一对应的用例集。</summary>
public class ClipboardChunkerTests
{
    [Fact]
    public void ShortTextSingleChunk()
    {
        var c = ClipboardChunker.Split("hello");
        var chunk = Assert.Single(c);
        Assert.Equal(0, chunk.Seq);
        Assert.Equal(1, chunk.Total);
        Assert.Equal("hello", chunk.Text);
    }

    [Fact]
    public void LongTextSplitsAndRoundTrips()
    {
        // 15 字节/组 × 8000 = 120000 字节，落在 64KB~256KB 之间 → 应分多片
        var text = string.Concat(Enumerable.Repeat("中文abc混合", 8000));
        var c = ClipboardChunker.Split(text);
        Assert.True(c.Count > 1, $"应分多片，实际 {c.Count}");
        Assert.Equal(c.Count, c[0].Total);
        Assert.Equal(text, string.Concat(c.OrderBy(x => x.Seq).Select(x => x.Text)));
    }

    [Fact]
    public void EachChunkUnderLimit()
    {
        var text = string.Concat(Enumerable.Repeat("中", 50000));
        foreach (var chunk in ClipboardChunker.Split(text))
        {
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(chunk.Text) <= ClipboardChunker.MaxChunkBytes,
                $"分片过大：{System.Text.Encoding.UTF8.GetByteCount(chunk.Text)}");
        }
    }

    [Fact]
    public void NoReplacementCharProduced()
    {
        var text = string.Concat(Enumerable.Repeat("汉字", 30000));
        foreach (var chunk in ClipboardChunker.Split(text))
        {
            Assert.DoesNotContain('\uFFFD', chunk.Text);
        }
    }

    [Fact]
    public void ExactlyOneChunkBoundary()
    {
        var text = new string('a', ClipboardChunker.MaxChunkBytes);
        var c = ClipboardChunker.Split(text);
        var chunk = Assert.Single(c);
        Assert.Equal(text, chunk.Text);
    }

    [Fact]
    public void OverTotalLimitReturnsEmpty()
    {
        var text = new string('a', ClipboardChunker.MaxTotalBytes + 1);
        Assert.Empty(ClipboardChunker.Split(text));
    }

    [Fact]
    public void EmptyTextSingleChunk()
    {
        var c = ClipboardChunker.Split("");
        var chunk = Assert.Single(c);
        Assert.Equal("", chunk.Text);
    }

    [Fact]
    public void AssemblerReturnsFullText()
    {
        var asm = new ClipboardAssembler();
        var text = string.Concat(Enumerable.Repeat("中文abc混合", 8000));
        var parts = ClipboardChunker.Split(text);
        string? result = null;
        foreach (var p in parts) result = asm.Add("id1", p.Seq, p.Total, p.Text);
        Assert.Equal(text, result);
    }

    [Fact]
    public void AssemblerHandlesOutOfOrder()
    {
        var asm = new ClipboardAssembler();
        var text = string.Concat(Enumerable.Repeat("abcd", 40000));
        var parts = ClipboardChunker.Split(text);
        parts.Reverse();
        string? result = null;
        foreach (var p in parts) result = asm.Add("id2", p.Seq, p.Total, p.Text);
        Assert.Equal(text, result);
    }

    [Fact]
    public void MissingChunkReturnsNull()
    {
        var asm = new ClipboardAssembler();
        Assert.Null(asm.Add("id3", 0, 3, "a"));
        Assert.Null(asm.Add("id3", 2, 3, "c"));
    }

    [Fact]
    public void TimeoutDropsWholeGroup()
    {
        var asm = new ClipboardAssembler();
        Assert.Null(asm.Add("id4", 0, 2, "a", nowMs: 0));
        Assert.Null(asm.Add("id4", 1, 2, "b", nowMs: 10_000));
    }
}
