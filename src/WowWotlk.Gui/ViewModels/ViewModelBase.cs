using CommunityToolkit.Mvvm.ComponentModel;

namespace WowWotlk.Gui.ViewModels;

public class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Called every time this page becomes the visible one.
    ///
    /// The pages share one <c>AppSettings</c> and one client tree, and every view model is
    /// constructed once at startup — so a folder changed on Install, or an addon installed on
    /// Addons, is invisible to the other pages until something re-reads. Without this hook the
    /// only thing that re-reads is an app restart.
    /// </summary>
    public virtual void OnShown() { }
}
