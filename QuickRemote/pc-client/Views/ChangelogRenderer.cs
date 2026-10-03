using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 更新记录渲染器：把 CHANGELOG.md 的简单 Markdown 转成 FlowDocument。
/// 仅展示 PC 客户端相关的版本段——依据版本标题里**全部**半角括号内的组件名过滤，
/// 任一标签指向 PC 客户端即展示；Android / 中转服务器 / 关于页 的条目在 PC 端设置中心不显示。
/// 独立成类以便复用与离屏渲染验证。
/// </summary>
public static class ChangelogRenderer
{
    private static readonly Brush VersionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush StrongBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));
    private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(0x9B, 0xC4, 0xFF));
    private static readonly FontFamily MonoFont = new("Consolas, Courier New, monospace");

    /// <summary>将 Markdown 文本渲染为可直接赋给 FlowDocumentScrollViewer 的 FlowDocument。</summary>
    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            // 末尾挂 Segoe UI Emoji 兜底：CHANGELOG 里的 ⚠️/🎉 等表情不会渲染成方框
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Emoji"),
            FontSize = 13,
            Foreground = BodyBrush
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // 当前是否处于「需要展示」的版本段内（仅 PC 客户端相关）
        var included = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // 版本段标题形如：
            //   ## v1.1.3 (PC 客户端)                              单组件
            //   ## v1.1.67 (PC 客户端) + v1.0.78 (Android App)     双组件（PC 在前）
            //   ## v1.0.91 (Android App) + v1.1.80 (PC 客户端)     双组件（Android 在前）
            //   ## v1.0.87 (Android App) + v1.1.77 (PC 客户端) + relay 1.0.9
            //   ## v1.1.20                                            早期无标记（PC 单端）
            // 判定规则：扫描标题里**全部**半角括号标签，任一标签指向 PC 客户端即展示；
            // 全部标签都指向其他组件（Android / 中转服务器 / 关于页）才隐藏。
            // ⚠️ 不可只取第一个标签——合并段里 Android 排在前面时会整段被误隐藏，
            //    导致 PC 端看不到自己最新版本的记录。
            // 全角括号（如「（配合 PC v1.1.75）」）是补充说明而非组件标记，不参与判定。
            if (line.StartsWith("## "))
            {
                var heading = line[3..].Trim();
                var tags = System.Text.RegularExpressions.Regex.Matches(heading, @"\(([^)]*)\)")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Groups[1].Value.Trim())
                    .Where(t => t.Length > 0)
                    .ToList();

                if (tags.Count == 0)
                {
                    // 无组件标记：早期 PC 单端历史默认展示；但纯 relay / 关于页 段落仍需排除。
                    included = !MentionsNonPcComponent(heading);
                }
                else
                {
                    included = tags.Any(IsPcComponent);
                }
            }

            if (!included)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                doc.Blocks.Add(new Paragraph());
                continue;
            }

            if (line.StartsWith("### "))
            {
                doc.Blocks.Add(new Paragraph(new Run(line[4..].Trim()))
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 8, 0, 4),
                    Foreground = VersionBrush
                });
            }
            else if (line.StartsWith("## "))
            {
                doc.Blocks.Add(new Paragraph(new Run(line[3..].Trim()))
                {
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 6),
                    Foreground = VersionBrush
                });
            }
            else if (line.StartsWith("# "))
            {
                doc.Blocks.Add(new Paragraph(new Run(line[2..].Trim()))
                {
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 8),
                    Foreground = VersionBrush
                });
            }
            // 列表项 - 或 *
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var p = new Paragraph { Margin = new Thickness(16, 2, 0, 2) };
                p.Inlines.Add(new Run("• ") { Foreground = VersionBrush });
                AppendInline(p, line[2..].Trim());
                doc.Blocks.Add(p);
            }
            // 分隔线 ---
            else if (line == "---" || line == "***")
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 6, 0, 6)
                });
            }
            // 普通段落
            else
            {
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AppendInline(p, line);
                doc.Blocks.Add(p);
            }
        }

        return doc;
    }

    /// <summary>组件标签是否指向 PC 客户端：含「PC」或「客户端」即认为是。</summary>
    private static bool IsPcComponent(string tag) =>
        tag.Contains("PC", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("客户端");

    /// <summary>
    /// 无组件标记的标题里若点名了非 PC 组件（relay / 中转服务器 / 关于页），则不属于 PC 端记录。
    /// 早期 <c>## v1.1.20</c> 这类纯 PC 单端历史返回 false，照常展示。
    /// </summary>
    private static bool MentionsNonPcComponent(string heading) =>
        heading.Contains("relay", StringComparison.OrdinalIgnoreCase) ||
        heading.Contains("中转", StringComparison.OrdinalIgnoreCase) ||
        heading.Contains("服务器", StringComparison.OrdinalIgnoreCase) ||
        heading.Contains("关于页", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 行内标记解析：**加粗** 与 `代码` 转成带样式的 Run，其余按纯文本。
    /// （旧「更新记录」窗口直接把星号/反引号当正文显示，这里一并修掉。）
    /// </summary>
    private static void AppendInline(Paragraph p, string text)
    {
        var i = 0;
        var plain = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            p.Inlines.Add(new Run(BreakLongText(plain.ToString())));
            plain.Clear();
        }

        while (i < text.Length)
        {
            // **加粗**
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    p.Inlines.Add(new Run(BreakLongText(text[(i + 2)..end]))
                    {
                        FontWeight = FontWeights.SemiBold,
                        Foreground = StrongBrush
                    });
                    i = end + 2;
                    continue;
                }
            }

            // `代码`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    p.Inlines.Add(new Run(BreakLongText(text[(i + 1)..end]))
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeBrush
                    });
                    i = end + 1;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain();
    }

    /// <summary>
    /// 长串断行处理：无空格断行点的长文本（URL、超长英文词等）会把 FlowDocument
    /// 的最小宽度撑大，导致内容宽度固定不跟随容器。每 24 字符插入零宽空格
    /// （U+200B）提供断行机会；内容在容器内重排，宽度跟随面板。
    /// </summary>
    private static string BreakLongText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= 24) return text;
        var sb = new System.Text.StringBuilder(text.Length + text.Length / 24);
        int count = 0;
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (++count % 24 == 0 && count < text.Length) sb.Append('\u200B');
        }
        return sb.ToString();
    }
}
