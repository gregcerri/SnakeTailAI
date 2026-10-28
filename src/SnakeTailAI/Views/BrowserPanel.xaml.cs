using System.Windows.Controls;
using SnakeTailAI.ViewModels;

namespace SnakeTailAI.Views;

public partial class BrowserPanel : UserControl
{
    public BrowserPanel()
    {
        InitializeComponent();
        MouseEnter += (_, _) => SetPointerOver(true);
        MouseLeave += (_, _) => SetPointerOver(false);
    }

    private void SetPointerOver(bool over)
    {
        if (DataContext is BrowserViewModel browser)
            browser.IsPointerOver = over;
    }
}
