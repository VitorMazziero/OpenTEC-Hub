using CommunityToolkit.Mvvm.ComponentModel;
using TecnalHub.Services.Recipes;

namespace TecnalHub.ViewModels;

/// <summary>
/// One editable parameter field, generated from a block's <see cref="RecipeParameter"/> schema.
/// </summary>
/// <remarks>
/// This is the "declared once and generated" property editor: rather than a hand-written control
/// per node type, every field is projected from the catalog schema, reading and writing the node's
/// <see cref="RecipeNode.Parameters"/> JSON. The <c>IsNumber/IsEnum/IsBool/IsText</c> flags let the
/// view pick the right control with a data trigger.
/// </remarks>
public sealed partial class RecipeParameterFieldViewModel : ObservableObject
{
    private readonly RecipeNode _node;
    private readonly Action _onChanged;

    public RecipeParameterFieldViewModel(RecipeNode node, RecipeParameter parameter, Action onChanged)
    {
        _node = node;
        _onChanged = onChanged;
        Parameter = parameter;
        IsVisible = EvaluateVisibility();
    }

    public RecipeParameter Parameter { get; }

    public string Key => Parameter.Key;

    public string Label => Parameter.Label;

    public string? Unit => Parameter.Unit;

    public string? Group => Parameter.Group;

    public IReadOnlyList<RecipeOption> Options => Parameter.Options;

    public bool IsNumber => Parameter.Kind is ParameterKind.Number or ParameterKind.Integer;

    public bool IsEnum => Parameter.Kind is ParameterKind.Enum;

    public bool IsBool => Parameter.Kind is ParameterKind.Bool;

    public bool IsText => Parameter.Kind is ParameterKind.Text;

    public bool IsList => Parameter.Kind is ParameterKind.List;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <summary>Numeric value, bound for Number/Integer fields.</summary>
    public double NumberValue
    {
        get => _node.Number(Key);
        set
        {
            _node.Set(Key, value);
            _onChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>Boolean value, bound for Bool fields.</summary>
    public bool BoolValue
    {
        get => _node.Flag(Key);
        set
        {
            _node.Set(Key, value);
            _onChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>Free text, bound for Text fields.</summary>
    public string TextValue
    {
        get => _node.Text(Key);
        set
        {
            _node.Set(Key, value);
            _onChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>Selected option, bound for Enum fields.</summary>
    public RecipeOption? SelectedOption
    {
        get => Options.FirstOrDefault(o => o.Value == _node.Text(Key)) ?? Options.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                _node.Set(Key, value.Value);
                _onChanged();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Re-evaluates the <c>VisibleWhen</c> guard after a sibling field changed.</summary>
    public void RefreshVisibility() => IsVisible = EvaluateVisibility();

    private bool EvaluateVisibility()
    {
        if (Parameter.VisibleWhen is not { } guard)
        {
            return true;
        }

        var parts = guard.Split('=', 2);
        return parts.Length == 2 && _node.Text(parts[0]) == parts[1];
    }
}

/// <summary>A port anchor on a node, for drawing connectors and hit-testing.</summary>
public sealed class RecipePortViewModel
{
    public RecipePortViewModel(RecipePort port, double offsetY)
    {
        Port = port;
        OffsetY = offsetY;
    }

    public RecipePort Port { get; }

    public string Name => Port.Name;

    public string Label => Port.Label ?? Port.Name;

    public bool IsInput => Port.Direction == PortDirection.In;

    public bool IsLoop => ConnectorNames.IsLoopIn(Port.Name) || ConnectorNames.IsLoopOut(Port.Name);

    /// <summary>Vertical offset of the port within the node body.</summary>
    public double OffsetY { get; }

    /// <summary>Horizontal anchor: the left edge for inputs, the right edge for outputs.</summary>
    public double OffsetX => IsInput ? 0 : RecipeNodeViewModel.Width;
}

/// <summary>
/// A node on the recipe canvas: its position, category header, generated fields and live execution
/// state. Wraps a <see cref="RecipeNode"/> and writes position changes straight back to it.
/// </summary>
public sealed partial class RecipeNodeViewModel : ObservableObject
{
    /// <summary>Canvas node width, shared with the port anchor maths.</summary>
    public const double Width = 234;

    /// <summary>Header band height.</summary>
    public const double HeaderHeight = 34;

    private const double PortPitch = 22;
    private const double BodyPadding = 12;

    public RecipeNodeViewModel(RecipeNode model)
    {
        Model = model;
        Title = model.Definition.Title;
        HeaderColor = RecipeNodeCatalog.HeaderColor(model.Type);
        Fields = [.. model.Definition.Parameters.Select(p => new RecipeParameterFieldViewModel(model, p, OnFieldChanged))];
        Ports = BuildPorts(model);
        X = model.X;
        Y = model.Y;
        Summary = BuildSummary();
    }

    public RecipeNode Model { get; }

    public string Id => Model.Id;

    public NodeType Type => Model.Type;

    public string Title { get; }

    /// <summary>Category header colour (hex), applied to the header band only.</summary>
    public string HeaderColor { get; }

    public IReadOnlyList<RecipeParameterFieldViewModel> Fields { get; }

    public IReadOnlyList<RecipePortViewModel> Ports { get; }

    /// <summary>The visible fields (respecting each field's <c>VisibleWhen</c> guard).</summary>
    public IEnumerable<RecipeParameterFieldViewModel> VisibleFields => Fields.Where(f => f.IsVisible);

    /// <summary>Node body height, from the larger of its inbound/outbound port stacks.</summary>
    public double Height
    {
        get
        {
            var inputs = Ports.Count(p => p.IsInput);
            var outputs = Ports.Count(p => !p.IsInput);
            return HeaderHeight + BodyPadding * 2 + Math.Max(1, Math.Max(inputs, outputs)) * PortPitch;
        }
    }

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial NodeState ExecutionState { get; set; } = NodeState.Waiting;

    [ObservableProperty]
    public partial string Summary { get; set; }

    /// <summary>Raised when a field value or the position changes, so the page re-validates and re-serialises.</summary>
    public event Action? Changed;

    partial void OnXChanged(double value)
    {
        Model.X = value;
        Changed?.Invoke();
    }

    partial void OnYChanged(double value)
    {
        Model.Y = value;
        Changed?.Invoke();
    }

    private void OnFieldChanged()
    {
        foreach (var field in Fields)
        {
            field.RefreshVisibility();
        }

        Summary = BuildSummary();
        OnPropertyChanged(nameof(VisibleFields));
        Changed?.Invoke();
    }

    private static IReadOnlyList<RecipePortViewModel> BuildPorts(RecipeNode model)
    {
        var ports = new List<RecipePortViewModel>();
        var inputs = 0;
        var outputs = 0;
        foreach (var port in model.Definition.Ports)
        {
            var index = port.Direction == PortDirection.In ? inputs++ : outputs++;
            var offsetY = HeaderHeight + BodyPadding + index * PortPitch + PortPitch / 2;
            ports.Add(new RecipePortViewModel(port, offsetY));
        }

        return ports;
    }

    /// <summary>A short pt-BR summary line under the header, mirroring the block's key parameters.</summary>
    private string BuildSummary() => Type switch
    {
        NodeType.Timer => $"Aguardar {Model.Number("duracao"):0.##} {OptionLabel("unidade")}",
        NodeType.MonitorVariable => $"{OptionLabel("variavel")} {OptionLabel("condicao")} {Model.Number("valorAlvo"):0.##}",
        NodeType.SetSetpoint => $"{OptionLabel("variavel")} → {Model.Number("valor"):0.##}",
        NodeType.SetLoop => $"{OptionLabel("operacao")} {OptionLabel("malha")}",
        NodeType.CascadeControl => $"Cascata O₂: SP {Model.Number("spO2"):0.#} %",
        NodeType.ManualIntervention => OptionLabel("operacao"),
        NodeType.LogEvent => Model.Text("mensagem"),
        NodeType.PhPump => $"Bomba pH: {OptionLabel("operacao")}",
        NodeType.AntifoamPump => $"Antiespuma: {OptionLabel("operacao")}",
        NodeType.NutrientPump => $"Nutrientes: {OptionLabel("operacao")}",
        _ => Model.Definition.Title,
    };

    /// <summary>The pt-BR label for the option a parameter currently holds (falls back to the raw value).</summary>
    private string OptionLabel(string key)
    {
        var value = Model.Text(key);
        return Model.Definition.Parameter(key)?.Options.FirstOrDefault(o => o.Value == value)?.Label ?? value;
    }
}
