using Syncfusion.Maui.Toolkit.SegmentedControl;

namespace InfraAdvisor.Mobile.Controls;

/// <summary>
/// App-owned XAML surface for Syncfusion's backend selector. The direct base-constructor and settings references keep the third-party constructors visible to the linked iOS build, where reflection-only XAML activation can otherwise be trimmed.
/// </summary>
public sealed class BackendSegmentedControl : SfSegmentedControl
{
    public BackendSegmentedControl()
    {
        SelectionIndicatorSettings = new SelectionIndicatorSettings
        {
            Background = new SolidColorBrush(Color.FromArgb("#1D4ED8")),
            TextColor = Colors.White,
        };
    }
}
