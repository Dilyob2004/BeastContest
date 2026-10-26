using BeastContest.Web.Services;
using Microsoft.AspNetCore.Components;
namespace BeastContest.Web.Components;

public abstract class LocalizedComponent : ComponentBase, IDisposable
{
    [Inject] protected UiState Ui { get; set; } = default!;
    protected override void OnInitialized() => Ui.Changed += Refresh;
    private void Refresh() => _ = InvokeAsync(StateHasChanged);
    public virtual void Dispose() { Ui.Changed -= Refresh; GC.SuppressFinalize(this); }
}
