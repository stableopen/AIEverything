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
        ResultsHeader.Visibility = Visibility.Collapsed;
        ResultsItems.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在建立正文索引，文件名搜索已可用。";
    }
}
