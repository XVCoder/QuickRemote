using System;
using System.Collections.Generic;
using System.Text;

namespace QuickRemote.PCClient.Interop;

/// <summary>
/// 剪贴板文本分片器。与 Android 端 services/ClipboardChunker.kt 共享同一套
/// 常量与规则（单片 64KB / 总量 256KB / 码点安全切割），改动需同步两端。
/// </summary>
public static class ClipboardChunker
{
    /// <summary>单片上限（UTF-8 字节）。</summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>单次同步总量上限（UTF-8 字节），超出直接放弃而非截断。</summary>
    public const int MaxTotalBytes = 256 * 1024;

    public readonly record struct Chunk(int Seq, int Total, string Text);

    /// <summary>
    /// 分片。总量超限时返回空列表（调用方应放弃本次同步）。
    /// 空文本返回单片，保证"复制了空内容"也能正常同步。
    /// </summary>
    public static List<Chunk> Split(string text)
    {
        var result = new List<Chunk>();
        if (Encoding.UTF8.GetByteCount(text) > MaxTotalBytes) return result;

        var pieces = new List<string>();
        var pieceStart = 0;
        var bytesInPiece = 0;

        var i = 0;
        while (i < text.Length)
        {
            // 孤立代理项（非法 UTF-16）按单 char 处理：GetByteCount 会把它编成
            // U+FFFD 的 3 字节，这里保持一致的计数口径，避免切割点算错。
            int charCount;
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                charCount = 2;
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
            }
            else
            {
                charCount = 1;
                codePoint = text[i];
            }

            var segBytes = Utf8Length(codePoint);
            if (bytesInPiece + segBytes > MaxChunkBytes && bytesInPiece > 0)
            {
                pieces.Add(text.Substring(pieceStart, i - pieceStart));
                pieceStart = i;
                bytesInPiece = 0;
            }
            bytesInPiece += segBytes;
            i += charCount;
        }
        pieces.Add(text.Substring(pieceStart));

        var total = pieces.Count;
        for (var idx = 0; idx < total; idx++)
        {
            result.Add(new Chunk(idx, total, pieces[idx]));
        }
        return result;
    }

    /// <summary>UTF-8 编码后该码点占用的字节数。</summary>
    private static int Utf8Length(int codePoint) => codePoint switch
    {
        < 0x80 => 1,
        < 0x800 => 2,
        < 0x10000 => 3,
        _ => 4
    };
}

/// <summary>
/// 剪贴板分片组装器。按 id 归组，收齐后按 seq 顺序拼接；
/// 超过 timeoutMs 仍未收齐的组会被丢弃，避免断流后残留的半个载荷被误拼。
/// </summary>
public sealed class ClipboardAssembler
{
    private sealed class Pending
    {
        public readonly int Total;
        public readonly Dictionary<int, string> Parts = new();
        public readonly long StartedAt;

        public Pending(int total, long startedAt)
        {
            Total = total;
            StartedAt = startedAt;
        }
    }

    private readonly Dictionary<string, Pending> _pending = new();
    private readonly long _timeoutMs;

    public ClipboardAssembler(long timeoutMs = 5_000) => _timeoutMs = timeoutMs;

    /// <summary>加入一片。集齐返回全文，否则返回 null。</summary>
    public string? Add(string id, int seq, int total, string text, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PurgeExpired(now);

        if (total <= 0 || seq < 0 || seq >= total) return null;

        // 同 id 但 total 不一致 → 视为对端开了新一组，丢弃旧的
        if (!_pending.TryGetValue(id, out var entry) || entry.Total != total)
        {
            entry = new Pending(total, now);
            _pending[id] = entry;
        }
        entry.Parts[seq] = text;

        if (entry.Parts.Count < entry.Total) return null;

        var sb = new StringBuilder();
        for (var i = 0; i < entry.Total; i++)
        {
            if (!entry.Parts.TryGetValue(i, out var part)) return null;
            sb.Append(part);
        }
        _pending.Remove(id);
        return sb.ToString();
    }

    private void PurgeExpired(long now)
    {
        List<string>? stale = null;
        foreach (var kv in _pending)
        {
            if (now - kv.Value.StartedAt > _timeoutMs)
            {
                (stale ??= new List<string>()).Add(kv.Key);
            }
        }
        if (stale == null) return;
        foreach (var key in stale) _pending.Remove(key);
    }
}
