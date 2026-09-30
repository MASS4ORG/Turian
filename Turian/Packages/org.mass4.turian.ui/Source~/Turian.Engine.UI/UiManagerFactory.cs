[assembly: Turian.Engine.Core.UiPresenterFactory(typeof(Turian.Engine.UI.UiManagerFactory))]

namespace Turian.Engine.UI;

/// <summary>Gives hosts a <see cref="UiManager"/> without naming it: the interface package's <see cref="IUiPresenterFactory"/>.</summary>
public sealed class UiManagerFactory : IUiPresenterFactory
{
    /// <inheritdoc/>
    public IUiPresenter Create(Vulkan vulkan, IInputSource? input, LocaleService? locale) =>
        new UiManager(vulkan, input, locale);
}
