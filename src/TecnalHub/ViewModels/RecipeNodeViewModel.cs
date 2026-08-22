using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
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
public sealed partial class RecipePortViewModel : ObservableObject
{
    public RecipePortViewModel(RecipePort port, double offsetY)
    {
        Port = port;
        OffsetY = offsetY;
    }

    public RecipePort Port { get; }

    public string Name => Port.Name;

    public bool IsInput => Port.Direction == PortDirection.In;

    public bool IsLoop => ConnectorNames.IsLoopIn(Port.Name) || ConnectorNames.IsLoopOut(Port.Name);

    public bool IsOnLeft => IsInput || IsLoop;

    public string Label => Port.Label ?? (Name switch
    {
        ConnectorNames.In => "Entrada",
        ConnectorNames.Out => "Saída",
        ConnectorNames.LoopOut => "Saída do Loop",
        ConnectorNames.LoopIn => "Entrada do Loop",
        _ => Port.Name
    });

    /// <summary>Vertical offset of the port within the node body.</summary>
    [ObservableProperty]
    public partial double OffsetY { get; set; }

    /// <summary>Horizontal anchor: the left edge for inputs and loop ports, the right edge for other outputs.</summary>
    public double OffsetX => IsOnLeft ? 0 : RecipeNodeViewModel.Width;

    [ObservableProperty]
    public partial bool IsConnectableTarget { get; set; }
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
        UpdatePortOffsets();
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

    public bool ShowSummary => Type is not (NodeType.Start or NodeType.End or NodeType.And or NodeType.Or or NodeType.ManualIntervention);

    public bool IsManualIntervention => Type == NodeType.ManualIntervention;

    public string ManualButtonText => IsCascadeLoopCondition
        ? (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "PULAR CASCATA" : "CONTINUAR CASCATA")
        : (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "PASSAR" : "BLOQUEAR");

    public string ManualButtonIcon => IsCascadeLoopCondition
        ? (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "⏩" : "▶")
        : (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "▶" : "🔒");

    public string ManualButtonColor => IsCascadeLoopCondition
        ? (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "#2563D9" : "#16A34A")
        : (Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "#16A34A" : "#DC2626");

    public string ManualExplanationText => IsCascadeLoopCondition
        ? "No loop da cascata: em Continuar Cascata a cascata opera; mude para Pular Cascata para encerrá-la e avançar ao próximo bloco (ajustável ao vivo durante a execução)."
        : "Bloqueado: a receita fica em standby neste bloco até Passar (ajustável ao vivo durante a execução).";

    [RelayCommand]
    public void ToggleManualGate()
    {
        var current = Model.Enum<ManualGateOperation>("operacao");
        var next = current == ManualGateOperation.Hold ? ManualGateOperation.Pass : ManualGateOperation.Hold;
        Model.Parameters["operacao"] = next.ToString();
        if (Fields.FirstOrDefault(f => f.Key == "operacao") is { } field)
        {
            field.TextValue = next.ToString();
        }

        OnPropertyChanged(nameof(ManualButtonText));
        OnPropertyChanged(nameof(ManualButtonIcon));
        OnPropertyChanged(nameof(ManualButtonColor));
        OnPropertyChanged(nameof(ManualExplanationText));
        Summary = BuildSummary();
        Changed?.Invoke();
    }

    public double Height
    {
        get
        {
            if (Type == NodeType.CascadeControl)
            {
                return HeaderHeight + 12 + 2 * 18 + 12 + 48 + 16;
            }

            if (Type is NodeType.Start or NodeType.End or NodeType.And or NodeType.Or)
            {
                return 70;
            }

            if (IsManualIntervention)
            {
                return 155;
            }

            if (Type is NodeType.MultiSetpoint or NodeType.MultiLoop)
            {
                var count = Type == NodeType.MultiSetpoint
                    ? (Model.Parameters["pontos"] is JsonArray a ? a.Count : 0)
                    : (Model.Parameters["controles"] is JsonArray c ? c.Count : 0);
                var lineCount = Math.Max(1, count);
                return HeaderHeight + 12 + lineCount * 18 + 32;
            }

            if (Type is NodeType.PhPump or NodeType.AntifoamPump or NodeType.NutrientPump or NodeType.PumpControl)
            {
                var lineCount = Math.Max(1, Model.Definition.Parameters.Count);
                return HeaderHeight + 12 + lineCount * 18 + 32;
            }

            // Single property blocks (Timer, MonitorVariable, SetSetpoint, SetLoop, DataAcquisition, LogEvent, ResetVariables)
            return HeaderHeight + 12 + 18 + 32;
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

        OnPropertyChanged(nameof(ManualButtonText));
        OnPropertyChanged(nameof(ManualButtonIcon));
        OnPropertyChanged(nameof(ManualButtonColor));
        OnPropertyChanged(nameof(ManualExplanationText));
        Summary = BuildSummary();
    }

    public bool IsCascadeControl => Type == NodeType.CascadeControl;

    public double CascadeSpO2
    {
        get => Model.Number("spO2");
        set => SetParam("spO2", value);
    }

    public bool CascadeAtuadorAgitacao
    {
        get => Model.Flag("atuadorAgitacao");
        set => SetParam("atuadorAgitacao", value);
    }

    public bool CascadeAtuadorAeracao
    {
        get => Model.Flag("atuadorAeracao");
        set => SetParam("atuadorAeracao", value);
    }

    public double CascadeAgitOutMin
    {
        get => Model.Number("agitacaoOutMin");
        set => SetParam("agitacaoOutMin", value);
    }

    public double CascadeAgitOutMax
    {
        get => Model.Number("agitacaoOutMax");
        set => SetParam("agitacaoOutMax", value);
    }

    public double CascadeAerOutMin
    {
        get => Model.Number("aeracaoOutMin");
        set => SetParam("aeracaoOutMin", value);
    }

    public double CascadeAerOutMax
    {
        get => Model.Number("aeracaoOutMax");
        set => SetParam("aeracaoOutMax", value);
    }

    public GridLength AgitationBarStart => new(Math.Clamp(CascadeAgitOutMin, 0, 100), GridUnitType.Star);
    public GridLength AgitationBarSpan => new(Math.Clamp(CascadeAgitOutMax - CascadeAgitOutMin, 0, 100 - Math.Clamp(CascadeAgitOutMin, 0, 100)), GridUnitType.Star);
    public GridLength AgitationBarRest => new(Math.Max(0.001, 100 - Math.Clamp(CascadeAgitOutMin, 0, 100) - Math.Clamp(CascadeAgitOutMax - CascadeAgitOutMin, 0, 100 - Math.Clamp(CascadeAgitOutMin, 0, 100))), GridUnitType.Star);

    public GridLength AerationBarStart => new(Math.Clamp(CascadeAerOutMin, 0, 100), GridUnitType.Star);
    public GridLength AerationBarSpan => new(Math.Clamp(CascadeAerOutMax - CascadeAerOutMin, 0, 100 - Math.Clamp(CascadeAerOutMin, 0, 100)), GridUnitType.Star);
    public GridLength AerationBarRest => new(Math.Max(0.001, 100 - Math.Clamp(CascadeAerOutMin, 0, 100) - Math.Clamp(CascadeAerOutMax - CascadeAerOutMin, 0, 100 - Math.Clamp(CascadeAerOutMin, 0, 100))), GridUnitType.Star);

    public string CascadeOverlapSummary
    {
        get
        {
            var ovMin = Math.Max(CascadeAgitOutMin, CascadeAerOutMin);
            var ovMax = Math.Min(CascadeAgitOutMax, CascadeAerOutMax);
            if (ovMax > ovMin)
            {
                return $"✦ Faixa de sobreposição: {ovMin:0.#}% a {ovMax:0.#}% (Agitação e Aeração atuam juntas)";
            }

            return "Atuação sequencial (sem sobreposição simultânea).";
        }
    }

    public IEnumerable<RecipeParameterFieldViewModel> CascadeAdvancedFields =>
        Fields.Where(f => f.IsVisible && f.Key is not ("spO2" or "atuadorAgitacao" or "atuadorAeracao" or "agitacaoOutMin" or "agitacaoOutMax" or "aeracaoOutMin" or "aeracaoOutMax"));

    private void SetParam(string key, object value)
    {
        if (value is double d)
        {
            Model.Parameters[key] = d;
        }
        else if (value is bool b)
        {
            Model.Parameters[key] = b;
        }
        else if (value is string s)
        {
            Model.Parameters[key] = s;
        }

        if (Fields.FirstOrDefault(f => f.Key == key) is { } field)
        {
            if (value is double num) field.NumberValue = num;
            else if (value is bool flag) field.BoolValue = flag;
            else if (value is string str) field.TextValue = str;
        }

        OnFieldChanged();
    }

    private void OnFieldChanged()
    {
        foreach (var field in Fields)
        {
            field.RefreshVisibility();
        }

        UpdatePortOffsets();
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(ManualButtonText));
        OnPropertyChanged(nameof(ManualButtonIcon));
        OnPropertyChanged(nameof(ManualButtonColor));
        OnPropertyChanged(nameof(ManualExplanationText));
        OnPropertyChanged(nameof(CascadeSpO2));
        OnPropertyChanged(nameof(CascadeAtuadorAgitacao));
        OnPropertyChanged(nameof(CascadeAtuadorAeracao));
        OnPropertyChanged(nameof(CascadeAgitOutMin));
        OnPropertyChanged(nameof(CascadeAgitOutMax));
        OnPropertyChanged(nameof(CascadeAerOutMin));
        OnPropertyChanged(nameof(CascadeAerOutMax));
        OnPropertyChanged(nameof(AgitationBarStart));
        OnPropertyChanged(nameof(AgitationBarSpan));
        OnPropertyChanged(nameof(AgitationBarRest));
        OnPropertyChanged(nameof(AerationBarStart));
        OnPropertyChanged(nameof(AerationBarSpan));
        OnPropertyChanged(nameof(AerationBarRest));
        OnPropertyChanged(nameof(CascadeOverlapSummary));
        Summary = BuildSummary();
        OnPropertyChanged(nameof(VisibleFields));
        OnPropertyChanged(nameof(CascadeAdvancedFields));
        Changed?.Invoke();
    }

    private void UpdatePortOffsets()
    {
        if (Type == NodeType.CascadeControl)
        {
            var topOffset = HeaderHeight + 12 + 2 * 18 + 12;
            foreach (var port in Ports)
            {
                if (port.Port.Name == ConnectorNames.In)
                {
                    port.OffsetY = topOffset;
                }
                else if (port.Port.Name == ConnectorNames.LoopOut)
                {
                    port.OffsetY = topOffset + 24;
                }
                else if (port.Port.Name == ConnectorNames.LoopIn)
                {
                    port.OffsetY = topOffset + 48;
                }
                else if (port.Port.Name == ConnectorNames.Out)
                {
                    port.OffsetY = topOffset + 48;
                }
            }
            return;
        }

        var bottomOffset = Height - 16;
        foreach (var port in Ports)
        {
            port.OffsetY = bottomOffset;
        }
    }

    private static IReadOnlyList<RecipePortViewModel> BuildPorts(RecipeNode model)
    {
        var ports = new List<RecipePortViewModel>();
        var topOffset = HeaderHeight + 12 + 2 * 18 + 12;
        foreach (var port in model.Definition.Ports)
        {
            var offsetY = model.Type == NodeType.CascadeControl
                ? port.Name switch
                {
                    ConnectorNames.In => topOffset,
                    ConnectorNames.LoopOut => topOffset + 24,
                    ConnectorNames.LoopIn => topOffset + 48,
                    _ => topOffset + 48,
                }
                : 50;
            ports.Add(new RecipePortViewModel(port, offsetY));
        }

        return ports;
    }

    private string BuildSummary() => Type switch
    {
        NodeType.Timer => $"Aguardar {Model.Number("duracao"):0.##} {OptionLabel("unidade")}",
        NodeType.MonitorVariable => $"{OptionLabel("variavel")} {FormatCondition(Model.Enum<ComparisonOperator>("condicao"))} {Model.Number("valorAlvo"):0.##}",
        NodeType.SetSetpoint => $"{OptionLabel("variavel")} → {Model.Number("valor"):0.##}",
        NodeType.SetLoop => $"{OptionLabel("operacao")} {OptionLabel("malha")}",
        NodeType.CascadeControl => FormatCascade(Model),
        NodeType.ManualIntervention => IsCascadeLoopCondition
            ? Model.Enum<ManualGateOperation>("operacao") == ManualGateOperation.Pass ? "Pular Cascata" : "Continuar Cascata"
            : OptionLabel("operacao"),
        NodeType.LogEvent => Model.Text("mensagem"),
        NodeType.PhPump => FormatPump(Model),
        NodeType.AntifoamPump => FormatPump(Model),
        NodeType.NutrientPump => FormatPump(Model),
        NodeType.PumpControl => FormatPump(Model),
        NodeType.MultiSetpoint => FormatMultiSetpoint(Model),
        NodeType.MultiLoop => FormatMultiLoop(Model),
        _ => Model.Definition.Title,
    };

    private static string FormatCascade(RecipeNode node)
    {
        var sp = node.Number("spO2");
        var agit = node.Flag("atuadorAgitacao");
        var aer = node.Flag("atuadorAeracao");

        var modo = (agit, aer) switch
        {
            (true, true) => "Agitação + Aeração (Mapa)",
            (true, false) => "Agitação",
            (false, true) => "Aeração",
            (false, false) => "Nenhum",
        };

        return $"SP: {sp:0.##} %\nModo: {modo}";
    }

    private string FormatPump(RecipeNode node)
    {
        var lines = new List<string>();
        foreach (var param in node.Definition.Parameters)
        {
            var key = param.Key;
            var label = param.Label;
            if (param.Kind == ParameterKind.Enum)
            {
                var optLabel = OptionLabel(key);
                lines.Add($"{label}: {optLabel}");
            }
            else if (param.Kind is ParameterKind.Number or ParameterKind.Integer)
            {
                var val = node.Number(key);
                var unit = string.IsNullOrEmpty(param.Unit) ? "" : " " + param.Unit;
                lines.Add($"{label}: {val:0.##}{unit}");
            }
            else if (param.Kind == ParameterKind.Text)
            {
                var text = node.Text(key);
                if (!string.IsNullOrEmpty(text))
                {
                    lines.Add($"{label}: {text}");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static string FormatMultiSetpoint(RecipeNode node)
    {
        if (node.Parameters["pontos"] is not JsonArray array || array.Count == 0)
        {
            return "Nenhum ponto de ajuste";
        }

        var lines = new List<string>();
        var varOptions = RecipeNodeCatalog.Definition(NodeType.MultiSetpoint).Parameter("pontos")?.ItemSchema.FirstOrDefault(p => p.Key == "variavel")?.Options;

        foreach (var item in array.OfType<JsonObject>())
        {
            var varKey = item["variavel"]?.ToString() ?? "";
            var varLabel = varOptions?.FirstOrDefault(o => o.Value == varKey)?.Label ?? varKey;
            var val = item["valor"] is JsonValue v && RecipeNode.TryReadNumber(v, out var n) ? n : 0.0;
            lines.Add($"{varLabel} → {val:0.##}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatMultiLoop(RecipeNode node)
    {
        if (node.Parameters["controles"] is not JsonArray array || array.Count == 0)
        {
            return "Nenhum controle";
        }

        var lines = new List<string>();
        var schema = RecipeNodeCatalog.Definition(NodeType.MultiLoop).Parameter("controles")?.ItemSchema;
        var malhaOptions = schema?.FirstOrDefault(p => p.Key == "malha")?.Options;
        var opOptions = schema?.FirstOrDefault(p => p.Key == "operacao")?.Options;

        foreach (var item in array.OfType<JsonObject>())
        {
            var malhaKey = item["malha"]?.ToString() ?? "";
            var malhaLabel = malhaOptions?.FirstOrDefault(o => o.Value == malhaKey)?.Label ?? malhaKey;

            var opKey = item["operacao"]?.ToString() ?? "";
            var opLabel = opOptions?.FirstOrDefault(o => o.Value == opKey)?.Label ?? opKey;

            lines.Add($"{opLabel} {malhaLabel}");
        }

        return string.Join("\n", lines);
    }

    private string OptionLabel(string key)
    {
        var value = Model.Text(key);
        return Model.Definition.Parameter(key)?.Options.FirstOrDefault(o => o.Value == value)?.Label ?? value;
    }

    private static string FormatCondition(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.GreaterOrEqual => "≥",
        ComparisonOperator.LessOrEqual => "≤",
        ComparisonOperator.Equal => "=",
        _ => ">=",
    };
}
