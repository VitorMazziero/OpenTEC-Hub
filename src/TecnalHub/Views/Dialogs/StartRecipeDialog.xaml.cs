using System.Windows;
using TecnalHub.Services.Dialogs;

namespace TecnalHub.Views.Dialogs;

public partial class StartRecipeDialog : Window
{
    public StartRecipeDialog(string recipeName)
    {
        InitializeComponent();
        RecipeName = recipeName;
        Subtitle = $"Receita selecionada: “{recipeName}”";
        DataContext = this;
    }

    public string RecipeName { get; }
    public string Subtitle { get; }
    public RecipeStartOption Result { get; private set; } = RecipeStartOption.Cancel;

    private void ResetAndStart_Click(object sender, RoutedEventArgs e)
    {
        Result = RecipeStartOption.ResetAndStart;
        DialogResult = true;
        Close();
    }

    private void StartPreserving_Click(object sender, RoutedEventArgs e)
    {
        Result = RecipeStartOption.StartPreserving;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = RecipeStartOption.Cancel;
        DialogResult = false;
        Close();
    }
}
