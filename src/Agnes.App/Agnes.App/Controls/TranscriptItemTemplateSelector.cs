using Agnes.Ui.Core.Transcript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App.Controls;

/// <summary>Picks the right DataTemplate for each kind of transcript item.</summary>
public sealed partial class TranscriptItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Message { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? Plan { get; set; }
    public DataTemplate? Permission { get; set; }
    public DataTemplate? Notice { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MessageBubbleItem => Message,
        ToolCallItem => Tool,
        PlanItemView => Plan,
        PermissionItem => Permission,
        NoticeItem => Notice,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
