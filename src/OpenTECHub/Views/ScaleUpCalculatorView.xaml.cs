using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OpenTECHub.ViewModels;

namespace OpenTECHub.Views;

/// <summary>Bioprocess sizing sheet for a new scale (§18.3 step 7).</summary>
public partial class ScaleUpCalculatorView : UserControl
{
    public ScaleUpCalculatorView()
    {
        InitializeComponent();
    }

    private BioprocessScaleUpViewModel? ViewModel => DataContext as BioprocessScaleUpViewModel;

    private void ExportSheet_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !viewModel.HasResult)
        {
            MessageBox.Show(
                "Calcule o dimensionamento antes de exportar.",
                "Exportar dimensionamento",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar folha de dimensionamento",
            Filter = "CSV (*.csv)|*.csv",
            FileName = viewModel.SuggestedCsvFileName,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, viewModel.BuildSummaryCsv(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Não foi possível exportar a folha de dimensionamento.\n\n{ex.Message}",
                "Exportar dimensionamento",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Prints the sizing sheet. PDF comes from the OS printer ("Microsoft Print to PDF"), which
    /// keeps the app free of a PDF dependency for a once-per-project document.
    /// </summary>
    private void PrintSheet_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !viewModel.HasResult)
        {
            MessageBox.Show(
                "Calcule o dimensionamento antes de imprimir.",
                "Imprimir dimensionamento",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new System.Windows.Controls.PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var document = BuildPrintableSheet(viewModel, dialog.PrintableAreaWidth);
            dialog.PrintDocument(
                ((IDocumentPaginatorSource)document).DocumentPaginator,
                "Folha de dimensionamento de bioprocesso");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nao foi possivel imprimir a folha de dimensionamento." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Imprimir dimensionamento",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static FlowDocument BuildPrintableSheet(BioprocessScaleUpViewModel viewModel, double width)
    {
        var document = new FlowDocument
        {
            PageWidth = width,
            ColumnWidth = width,
            PagePadding = new Thickness(48),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = Brushes.Black,
            Background = Brushes.White,
        };

        document.Blocks.Add(new Paragraph(new Run("Folha de dimensionamento de bioprocesso"))
        {
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        document.Blocks.Add(new Paragraph(new Run($"OpenTEC-Hub · {DateTime.Now:dd/MM/yyyy HH:mm}"))
        {
            FontSize = 10,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        });

        document.Blocks.Add(new Paragraph(new Run($"Referência: {viewModel.ReferenceSummary}"))
        {
            Margin = new Thickness(0, 0, 0, 4),
        });

        var criterion = viewModel.AvailableCriteria.FirstOrDefault(c => c.Criterion == viewModel.SelectedCriterion);
        var gasRule = viewModel.AvailableGasRules.FirstOrDefault(r => r.Rule == viewModel.SelectedGasRule);
        document.Blocks.Add(new Paragraph(new Run(
            $"Critério: {criterion?.DisplayText ?? viewModel.SelectedCriterion.ToString()} · " +
            $"Regra de gás: {gasRule?.DisplayText ?? viewModel.SelectedGasRule.ToString()} " +
            $"({viewModel.GasRuleValue:F3} {viewModel.GasRuleUnit})"))
        {
            Margin = new Thickness(0, 0, 0, 12),
        });

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
        for (var index = 0; index < 4; index++)
        {
            table.Columns.Add(new TableColumn
            {
                Width = index == 0 ? new GridLength(2.4, GridUnitType.Star) : new GridLength(1, GridUnitType.Star),
            });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        group.Rows.Add(BuildRow("Grandeza", "Referência", "Alvo", "Unidade", header: true));
        foreach (var row in viewModel.SheetRows)
        {
            group.Rows.Add(BuildRow(row.Quantity, row.Reference, row.Target, row.Unit, header: false));
        }

        document.Blocks.Add(table);

        if (viewModel.Warnings.Count > 0)
        {
            document.Blocks.Add(new Paragraph(new Run("Avisos"))
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 4),
            });

            var list = new List { MarkerStyle = TextMarkerStyle.Disc };
            foreach (var warning in viewModel.Warnings)
            {
                list.ListItems.Add(new ListItem(new Paragraph(new Run(warning))));
            }

            document.Blocks.Add(list);
        }

        document.Blocks.Add(new Paragraph(new Run(
            "Resultado de dimensionamento, calculado a partir de correlações ajustadas na escala de " +
            "referência. Não constitui validação do processo na nova escala."))
        {
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 12, 0, 0),
        });

        return document;
    }

    private static TableRow BuildRow(string first, string second, string third, string fourth, bool header)
    {
        var row = new TableRow();
        foreach (var text in new[] { first, second, third, fourth })
        {
            row.Cells.Add(new TableCell(new Paragraph(new Run(text)))
            {
                Padding = new Thickness(5, 3, 5, 3),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, header ? 1.2 : 0.4),
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
            });
        }

        return row;
    }
}
