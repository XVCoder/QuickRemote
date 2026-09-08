namespace QuickRemote.PCClient.Models;

/// <summary>设置页下拉选项（值 + 显示文本；ToString 供 ComboBox 展示）。</summary>
public sealed record OptionItem(int Value, string Label)
{
    public override string ToString() => Label;
}
