using System.Collections.Specialized;
using System.Windows.Controls;
using TecnalHub.ViewModels;

namespace TecnalHub.Views;

/// <summary>Unified app-known event trail, including exact command frames.</summary>
public partial class EventsView : UserControl
{
    private EventsViewModel? _viewModel;

    public EventsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_viewModel is not null)
        {
            _viewModel.VisibleEvents.CollectionChanged -= OnEventsChanged;
        }

        _viewModel = DataContext as EventsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.VisibleEvents.CollectionChanged += OnEventsChanged;
        }
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is { FollowNewEntries: true } && _viewModel.VisibleEvents.LastOrDefault() is { } newest)
        {
            EventList.ScrollIntoView(newest);
        }
    }
}
