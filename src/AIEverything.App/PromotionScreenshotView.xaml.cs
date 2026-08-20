using System.Windows;
using System.Windows.Controls;

namespace AIEverything.App;

public partial class PromotionScreenshotView : UserControl
{
    public PromotionScreenshotView(bool empty = false)
    {
        InitializeComponent();
        if (!empty)
        {
            return;
        }

        SearchQueryText.Text = string.Empty;
        EmptyContentBanner.Visibility = Visibility.Visible;
        ResultsCard.Margin = new Thickness(16, 58, 16, 12);
        ResultsHeader.Visibility = Visibility.Collapsed;
        ResultsItems.Visibility = Visibility.Collapsed;
        StatusText.Text = "文件名搜索已就绪。开启正文索引后可搜索 Word、TXT 和 Markdown。";
    }
}
