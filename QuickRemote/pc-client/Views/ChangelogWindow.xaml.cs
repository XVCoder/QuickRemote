using System.Net.Http;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using QuickRemote.PCClient.Services;

namespace QuickRemote.PCClient.Views;

/// <summary>
/// 更新记录窗口。从 quickdeploy 下载 CHANGELOG.md 并以 Markdown 格式展示。
/// </summary>
public partial class ChangelogWindow : Window
{
    private static ChangelogWindow? _instance;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ChangelogWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>显示更新记录窗口（单例）。</summary>
    public static void Show(string changelogUrl, Logger logger)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (_instance != null && _instance.IsLoaded)
            {
                _instance.Activate();
                return;
            }

            _instance = new ChangelogWindow();
            _instance.Show();
            await _instance.LoadAsync(changelogUrl, logger);
        });
    }

    private async Task LoadAsync(string changelogUrl, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(changelogUrl))
        {
            ShowStatus("未配置更新记录地址");
            return;
        }

        ShowStatus("加载中...");
        try
        {
            logger.Info($"Loading changelog from {changelogUrl}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var content = await Http.GetStringAsync(changelogUrl, cts.Token);

            var doc = MarkdownToFlowDocument(content);
            ChangelogViewer.Document = doc;
            ChangelogViewer.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            logger.Warn("Loading changelog timed out");
            ShowStatus("加载超时，请稍后重试");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to load changelog: {ex.Message}");
            ShowStatus($"加载失败：{ex.Message}");
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        ChangelogViewer.Visibility = Visibility.Collapsed;
    }

    /// <summary>将简单的 Markdown 文本转换为 FlowDocument。</summary>
    private static FlowDocument MarkdownToFlowDocument(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))
        };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // 当前是否处于「需要展示」的版本段内（仅 PC 客户端相关）
        var included = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // 版本段标题形如：## v1.1.3 (PC客户端) / ## v1.0.11 (Android App)
            // 依据括号内的组件名决定是否展示：PC 客户端 展示，Android/中转服务器 跳过，
            // 无组件标记的公共历史（如 v1.0.1 / v1.0.0）默认展示。
            if (line.StartsWith("## "))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line[3..].Trim(), @"^v?[\d.]+(?:\s*\(([^)]*)\))?");
                if (match.Success)
                {
                    var component = match.Groups[1].Value.Trim();
                    if (component.Length == 0)
                    {
                        included = true; // 公共历史段
                    }
                    else if (component.Contains("PC", StringComparison.OrdinalIgnoreCase) ||
                             component.Contains("客户端"))
                    {
                        included = true;
                    }
                    else
                    {
                        included = false; // Android / 中转服务器 等其他组件
                    }
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

            // 标题 ###
            if (line.StartsWith("### "))
            {
                var p = new Paragraph(new Run(line[4..].Trim()))
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 8, 0, 4),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D))
                };
                doc.Blocks.Add(p);
            }
            else if (line.StartsWith("## "))
            {
                var p = new Paragraph(new Run(line[3..].Trim()))
                {
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 6),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D))
                };
                doc.Blocks.Add(p);
            }
            else if (line.StartsWith("# "))
            {
                var p = new Paragraph(new Run(line[2..].Trim()))
                {
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 8),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D))
                };
                doc.Blocks.Add(p);
            }
            // 列表项 - 或 *
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var text = BreakLongText(line[2..].Trim());
                var p = new Paragraph
                {
                    Margin = new Thickness(16, 2, 0, 2)
                };
                p.Inlines.Add(new Run("• ") { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)) });
                p.Inlines.Add(new Run(text));
                doc.Blocks.Add(p);
            }
            // 分隔线 ---
            else if (line == "---" || line == "***")
            {
                var p = new Paragraph
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 6, 0, 6)
                };
                doc.Blocks.Add(p);
            }
            // 普通段落
            else
            {
                var p = new Paragraph(new Run(BreakLongText(line)))
                {
                    Margin = new Thickness(0, 2, 0, 2)
                };
                doc.Blocks.Add(p);
            }
        }

        return doc;
    }

    /// <summary>
    /// 长串断行处理：无空格断行点的长文本（URL、超长英文词等）会把 FlowDocument
    /// 的最小宽度撑大，导致窗口缩放时内容宽度固定不跟随。每 24 字符插入零宽空格
    /// （U+200B）提供断行机会；内容在窗口内重排，缩放跟随窗口宽度。
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

    protected override void OnClosed(EventArgs e)
    {
        _instance = null;
        base.OnClosed(e);
    }
}
