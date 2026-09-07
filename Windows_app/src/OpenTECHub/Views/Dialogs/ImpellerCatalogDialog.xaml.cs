using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using OpenTECHub.Services.PowerTesting;

namespace OpenTECHub.Views.Dialogs;

public partial class ImpellerCatalogDialog : Window
{
    private readonly ObservableCollection<Impeller> _catalog;
    private readonly Impeller? _targetStage;
    private readonly Action? _saveCatalogCallback;
    private readonly Action<Impeller>? _addToAssemblyCallback;

    public Impeller? SelectedResult { get; private set; }

    public ImpellerCatalogDialog(
        ObservableCollection<Impeller> catalog,
        Impeller? targetStage = null,
        Action? saveCatalogCallback = null,
        Action<Impeller>? addToAssemblyCallback = null)
    {
        InitializeComponent();
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _targetStage = targetStage;
        _saveCatalogCallback = saveCatalogCallback;
        _addToAssemblyCallback = addToAssemblyCallback;

        CatalogGrid.ItemsSource = _catalog;

        if (_targetStage != null)
        {
            // Mode: Selecting for a stage
            StageBanner.Visibility = Visibility.Visible;
            StageBannerText.Text = $"Configurando Estágio #{_targetStage.StageIndex} · Modelo atual: '{_targetStage.Label}'. Selecione um modelo da biblioteca ou dê duplo-clique para aplicar.";
            SelectButton.Visibility = Visibility.Visible;
            SelectButton.Content = $"✔ Aplicar ao Estágio #{_targetStage.StageIndex}";
            AddToAssemblyButton.Visibility = Visibility.Collapsed;

            // Pre-select the matching impeller if available, else the first
            var match = _catalog.FirstOrDefault(i => string.Equals(i.Label, _targetStage.Label, StringComparison.OrdinalIgnoreCase))
                        ?? _catalog.FirstOrDefault();
            CatalogGrid.SelectedItem = match;
            if (match != null)
            {
                CatalogGrid.ScrollIntoView(match);
            }
        }
        else
        {
            // Mode: General catalog management
            StageBanner.Visibility = Visibility.Collapsed;
            SelectButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Fechar";
            AddToAssemblyButton.Visibility = _addToAssemblyCallback != null ? Visibility.Visible : Visibility.Collapsed;

            CatalogGrid.SelectedItem = _catalog.FirstOrDefault();
        }
    }

    private Impeller? CurrentSelected => CatalogGrid.SelectedItem as Impeller;

    private void OnMinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var name = GetUniqueName("Novo Impelidor");
        var newItem = new Impeller
        {
            Type = ImpellerType.Custom,
            Label = name,
            DiameterM = 0.065,
            BladeCount = 6,
            ClearanceM = 0.065,
            LiteratureNp = 1.0,
        };

        _catalog.Add(newItem);
        CatalogGrid.SelectedItem = newItem;
        CatalogGrid.ScrollIntoView(newItem);
        StatusText.Text = $"Modelo '{name}' criado. Edite os parâmetros na tabela.";
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSelected is not { } source)
        {
            StatusText.Text = "Selecione um modelo para duplicar.";
            return;
        }

        var name = GetUniqueName($"{source.Label} (Cópia)");
        var duplicate = source.Clone();
        duplicate.Label = name;

        _catalog.Add(duplicate);
        CatalogGrid.SelectedItem = duplicate;
        CatalogGrid.ScrollIntoView(duplicate);
        StatusText.Text = $"Modelo duplicado como '{name}'.";
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSelected is not { } toRemove)
        {
            StatusText.Text = "Selecione um modelo para remover.";
            return;
        }

        if (_catalog.Count <= 1)
        {
            StatusText.Text = "O catálogo deve manter pelo menos um modelo de impelidor.";
            return;
        }

        var name = toRemove.Label;
        _catalog.Remove(toRemove);
        CatalogGrid.SelectedItem = _catalog.FirstOrDefault();
        StatusText.Text = $"Modelo '{name}' removido.";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_saveCatalogCallback != null)
            {
                _saveCatalogCallback.Invoke();
            }
            else
            {
                PowerImpellerCatalog.SaveCatalog(_catalog);
            }
            StatusText.Text = "Catálogo salvo em disco com sucesso.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao salvar catálogo: {ex.Message}";
        }
    }

    private void OnAddToAssemblyClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSelected is not { } selected)
        {
            StatusText.Text = "Selecione um modelo para adicionar ao eixo.";
            return;
        }

        _addToAssemblyCallback?.Invoke(selected);
        StatusText.Text = $"Modelo '{selected.Label}' adicionado ao eixo.";
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSelected is not { } selected)
        {
            StatusText.Text = "Selecione um modelo da tabela antes de confirmar.";
            return;
        }

        SelectedResult = selected;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCatalogGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CurrentSelected is null)
        {
            return;
        }

        if (_targetStage != null)
        {
            SelectedResult = CurrentSelected;
            DialogResult = true;
            Close();
        }
    }

    private string GetUniqueName(string baseName)
    {
        var name = baseName;
        var counter = 1;
        while (_catalog.Any(i => string.Equals(i.Label, name, StringComparison.OrdinalIgnoreCase)))
        {
            counter++;
            name = $"{baseName} {counter}";
        }
        return name;
    }
}
