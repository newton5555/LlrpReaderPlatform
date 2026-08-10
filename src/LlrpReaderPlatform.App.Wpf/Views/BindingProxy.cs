using System.Windows;

namespace LlrpReaderPlatform.App.Wpf.Views;

/// <summary>让 DataGridColumn 等非可视对象可以绑定 UserControl 的 DataContext。</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
