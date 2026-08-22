using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecnalHub.Services.Recipes;

namespace TecnalHub.ViewModels;

/// <summary>
/// One editable parameter field, generated from a block's <see cref="RecipeParameter"/> schema and
/// bound to a JSON value bag (a node's parameters, or a row of a repeating list).
/// </summary>
/// <remarks>
/// The "declared once and generated" property editor: every field is projected from the catalog
/// schema, reading and writing the <see cref="JsonObject"/> it was given. The
/// <c>IsNumber/IsEnum/IsBool/IsText/IsList</c> flags let the view pick the control with a trigger.
/// </remarks>
public sealed partial class RecipeParameterFieldViewModel : ObservableObject
{
    private readonly JsonObject _bag;
    private readonly Action _onChanged;

    public RecipeParameterFieldViewModel(JsonObject bag, RecipeParameter parameter, Action onChanged)
    {
        _bag = bag;
        _onChanged = onChanged;
        Parameter = parameter;
        Options = parameter.Options;
        IsVisible = EvaluateVisibility();

        if (parameter.Kind == ParameterKind.List)
        {
            RebuildRows();
        }
    }

    public RecipeParameter Parameter { get; }

    public string Key => Parameter.Key;

    public string Label => Parameter.Label;

    public string? Unit => Parameter.Unit;

    public string? Group => Parameter.Group;

    public bool IsNumber => Parameter.Kind is ParameterKind.Number or ParameterKind.Integer;

    public bool IsEnum => Parameter.Kind is ParameterKind.Enum;

    public bool IsBool => Parameter.Kind is ParameterKind.Bool;

    public bool IsText => Parameter.Kind is ParameterKind.Text;

    public bool IsList => Parameter.Kind is ParameterKind.List;

    /// <summary>Repeating-list rows, for <see cref="ParameterKind.List"/> parameters.</summary>
    public ObservableCollection<RecipeListRowViewModel> Rows { get; } = [];

    /// <summary>Options for an enum field; can be overridden for context-aware labels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOption))]
    public partial IReadOnlyList<RecipeOption> Options { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    public double NumberValue
    {
        get => ReadNumber(_bag, Key, Parameter);
        set { _bag[Key] = value; _onChanged(); OnPropertyChanged(); }
    }

    public bool BoolValue
    {
        get => _bag[Key] is JsonValue v && v.TryGetValue(out bool b) ? b : Parameter.Default is true;
        set { _bag[Key] = value; _onChanged(); OnPropertyChanged(); }
    }

    public string TextValue
    {
        get => _bag[Key] is JsonValue v && v.TryGetValue(out string? s) && s is not null ? s : Parameter.Default?.ToString() ?? "";
        set { _bag[Key] = value; _onChanged(); OnPropertyChanged(); }
    }

    public RecipeOption? SelectedOption
    {
        get
        {
            var value = TextValue;
            return Options.FirstOrDefault(o => o.Value == value) ?? Options.FirstOrDefault();
        }
        set
        {
            if (value is not null) { _bag[Key] = value.Value; _onChanged(); OnPropertyChanged(); }
        }
    }

    [RelayCommand]
    private void AddRow()
    {
        var array = ListArray();
        var row = new JsonObject();
        foreach (var item in Parameter.ItemSchema)
        {
            row[item.Key] = RecipeNode.DefaultValue(item);
        }

        array.Add(row);
        Rows.Add(new RecipeListRowViewModel(row, Parameter.ItemSchema, RemoveRow, _onChanged));
        _onChanged();
    }

    private void RemoveRow(RecipeListRowViewModel row)
    {
        var array = ListArray();
        var index = Rows.IndexOf(row);
        if (index >= 0 && index < array.Count)
        {
            array.RemoveAt(index);
            Rows.RemoveAt(index);
            _onChanged();
        }
    }

    private JsonArray ListArray()
    {
        if (_bag[Key] is not JsonArray array)
        {
            array = [];
            _bag[Key] = array;
        }

        return array;
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var element in ListArray().OfType<JsonObject>())
        {
            Rows.Add(new RecipeListRowViewModel(element, Parameter.ItemSchema, RemoveRow, _onChanged));
        }
    }

    public void RefreshVisibility() => IsVisible = EvaluateVisibility();

    private bool EvaluateVisibility()
    {
        if (Parameter.VisibleWhen is not { } guard)
        {
            return true;
        }

        var parts = guard.Split('=', 2);
        if (parts.Length != 2)
        {
            return true;
        }

        var current = _bag[parts[0]] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return current == parts[1];
    }

    internal static double ReadNumber(JsonObject bag, string key, RecipeParameter parameter)
    {
        if (bag[key] is JsonValue value && RecipeNode.TryReadNumber(value, out var number))
        {
            return number;
        }

        return Convert.ToDouble(parameter.Default ?? 0.0);
    }
}

/// <summary>One row of a repeating-list parameter (Múltiplos Pontos / Múltiplos Controles).</summary>
public sealed partial class RecipeListRowViewModel : ObservableObject
{
    private readonly Action<RecipeListRowViewModel> _remove;

    public RecipeListRowViewModel(
        JsonObject row, IReadOnlyList<RecipeParameter> schema, Action<RecipeListRowViewModel> remove, Action onChanged)
    {
        _remove = remove;
        Fields = [.. schema.Select(p => new RecipeParameterFieldViewModel(row, p, () => { onChanged(); RefreshVisibility(); }))];
    }

    public IReadOnlyList<RecipeParameterFieldViewModel> Fields { get; }

    public IEnumerable<RecipeParameterFieldViewModel> VisibleFields => Fields.Where(f => f.IsVisible);

    [RelayCommand]
    private void Remove() => _remove(this);

    private void RefreshVisibility()
    {
        foreach (var field in Fields)
        {
            field.RefreshVisibility();
        }

        OnPropertyChanged(nameof(VisibleFields));
    }
}

/// <summary>A port anchor on a node, for drawing connectors and hit-testing.</summary>
public sealed class RecipePortViewModel(RecipePort port, double offsetY)
{
    public RecipePort Port { get; } = port;

    public string Name => Port.Name;

    public string Label => Port.Label ?? Port.Name;

    public bool IsInput => Port.Direction == PortDirection.In;

    public bool IsLoop => ConnectorNames.IsLoopIn(Port.Name) || ConnectorNames.IsLoopOut(Port.Name);

    /// <summary>Vertical offset of the port within the node body.</summary>
    public double OffsetY { get; } = offsetY;

    /// <summary>Horizontal anchor: the left edge for inputs and loop ports, the right edge for other outputs.</summary>
    public double OffsetX => IsInput || IsLoop ? 0 : RecipeNodeViewModel.Width;
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

    private const double PortPitch = 24;
    private const double BodyPadding = 12;

    private static readonly RecipeOption[] LoopGateOptions =
    [
        new(nameof(ManualGateOperation.Hold), "Continuar Cascata"),
        new(nameof(ManualGateOperation.Pass), "Pular Cascata"),
    ];

    public RecipeNodeViewModel(RecipeNode model)
    {
        Model = model;
        Title = model.Definition.Title;
        HeaderColor = RecipeNodeCatalog.HeaderColor(model.Type);
        Fields = [.. model.Definition.Parameters.Select(p => new RecipeParameterFieldViewModel(model.Parameters, p, OnFieldChanged))];
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

    public IEnumerable<RecipeParameterFieldViewModel> VisibleFields => Fields.Where(f => f.IsVisible);

    public double Height
    {
        get
        {
            var left = Ports.Count(p => p.IsInput || p.IsLoop);
            var right = Ports.Count(p => !p.IsInput && !p.IsLoop);
            return HeaderHeight + BodyPadding * 2 + Math.Max(1, Math.Max(left, right)) * PortPitch;
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

    /// <summary>True when this block is the target of a cascade's Saída Loop (its exit condition).</summary>
    [ObservableProperty]
    public partial bool IsCascadeLoopCondition { get; set; }

    /// <summary>Raised when a field value or the position changes, so the tab re-validates.</summary>
    public event Action? Changed;

    partial void OnXChanged(double value) { Model.X = value; Changed?.Invoke(); }

    partial void OnYChanged(double value) { Model.Y = value; Changed?.Invoke(); }

    partial void OnIsCascadeLoopConditionChanged(bool value)
    {
        // An Intervenção Manual wired to the cascade's Saída Loop is the Continuar/Pular switch.
        if (Type == NodeType.ManualIntervention && Fields.FirstOrDefault(f => f.Key == "operacao") is { } field)
        {
            field.Options = value ? LoopGateOptions : RecipeNodeCatalog.Definition(Type).Parameter("operacao")!.Options;
        }

        Summary = BuildSummary();
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
        var leftCount = 0;
        var rightCount = 0;
        foreach (var port in model.Definition.Ports)
        {
            var isLeft = port.Direction == PortDirection.In
                         || ConnectorNames.IsLoopOut(port.Name)
                         || ConnectorNames.IsLoopIn(port.Name);
            var index = isLeft ? leftCount++ : rightCount++;
            var offsetY = HeaderHeight + BodyPadding + index * PortPitch + PortPitch / 2;
            ports.Add(new RecipePortViewModel(port, offsetY));
        }

        return ports;
    }

    private string BuildSummary() => Type switch
    {
        NodeType.Timer => $"Aguardar {Model.Number("duracao"):0.##} {OptionLabel("unidade")}",
        NodeType.MonitorVariable => $"{OptionLabel("variavel")} {OptionLabel("condicao")} {Model.Number("valorAlvo"):0.##}",
        NodeType.SetSetpoint => $"{OptionLabel("variavel")} → {Model.Number("valor"):0.##}",
        NodeType.SetLoop => $"{OptionLabel("operacao")} {OptionLabel("malha")}",
        NodeType.CascadeControl => $"Cascata O₂: SP {Model.Number("spO2"):0.#} %",
        NodeType.ManualIntervention => IsCascadeLoopCondition
            ? Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "Pular Cascata" : "Continuar Cascata"
            : OptionLabel("operacao"),
        NodeType.LogEvent => Model.Text("mensagem"),
        NodeType.PhPump => $"Bomba pH: {OptionLabel("operacao")}",
        NodeType.AntifoamPump => $"Antiespuma: {OptionLabel("operacao")}",
        NodeType.NutrientPump => $"Nutrientes: {OptionLabel("operacao")}",
        _ => Model.Definition.Title,
    };

    private string OptionLabel(string key)
    {
        var value = Model.Text(key);
        return Model.Definition.Parameter(key)?.Options.FirstOrDefault(o => o.Value == value)?.Label ?? value;
    }
}
