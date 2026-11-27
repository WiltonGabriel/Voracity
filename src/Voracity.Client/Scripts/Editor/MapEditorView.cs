using Godot;
using Voracity.Client.Map;
using Voracity.Core.Data;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Editor;

/// <summary>
/// Full Code-First In-Game World Cartography & Topology Editor for Voracity:
/// Operates directly on PlanarMapTopology + EditableWorldAtlas + AtlasCommandStack (Undo/Redo),
/// with live MapView2D hybrid GPU re-bake, SharedArc vertex dragging, Province Split/Merge,
/// Natural Features/Straits drawing, Isolated Territory & War Conflict validator,
/// Versioned Snapshots + Onion-Skin diff, Reference Image Overlay, and High-Res PNG + _delta.json export.
/// </summary>
public partial class MapEditorView : CanvasLayer
{
    public enum EditorToolMode
    {
        SelectInspect,
        EditBorderVertex,
        SplitProvince,
        PaintOwnerOrMerge,
        DrawGeographyFeature,
        DropAiPin
    }

    private readonly MapView2D _mapView;
    private readonly LocalizationCatalog _loc;
    private readonly AtlasCommandStack _cmdStack = new();

    private EditableWorldAtlas? _atlas;
    private AtlasSnapshotRecord? _baselineSnapshot;
    private string _dataRootPath = string.Empty;
    private string _versionsDir = string.Empty;
    private string _exportDir = string.Empty;

    private EditorToolMode _activeTool = EditorToolMode.SelectInspect;
    private string _paintNationId = "AET";
    private string? _selectedProvinceId;
    private string? _selectedStateId;
    private string? _pendingMergeSourceProvId;
    private string? _pendingStraitSourceProvId;
    private Point2D? _pendingSplitStart;
    private readonly List<Point2D> _pendingFeaturePoints = new();
    private string _pendingFeatureKind = "river";
    private bool _showOnionSkinDiff;

    // Border vertex dragging state
    private int _draggingArcId = -1;
    private int _draggingPointIdx = -1;

    // World-space Gizmo overlay + Reference image overlay attached inside MapView2D
    private EditorGizmoCanvas? _gizmoCanvas;
    private TextureRect? _referenceOverlayRect;

    // Code-First Editor UI panels
    private Label _statusBanner = null!;
    private Button _undoBtn = null!;
    private Button _redoBtn = null!;
    private OptionButton _paintNationPicker = null!;
    private OptionButton _featureKindPicker = null!;
    private Label _selectionHeader = null!;
    private LineEdit _provNameEdit = null!;
    private LineEdit _stateNameEdit = null!;
    private OptionButton _ownerAssignPicker = null!;
    private OptionButton _terrainPicker = null!;
    private OptionButton _hubPicker = null!;
    private Label _hierarchyMetaLabel = null!;
    private Label _validationLogLabel = null!;
    private OptionButton _versionListPicker = null!;
    private HSlider _refOpacitySlider = null!;
    private LineEdit _refPathEdit = null!;
    private FileDialog _refFileDialog = null!;

    public event Action? ExitEditorRequested;
    public event Action? WorldDataSavedRequested;

    public MapEditorView(MapView2D mapView, LocalizationCatalog loc)
    {
        _mapView = mapView;
        _loc = loc;
        Layer = 20;
    }

    public bool IsOpen { get; private set; }

    public override void _Ready()
    {
        Visible = false;
        BuildUi();
    }

    public void OpenEditor(string dataRootPath, string activeLang)
    {
        _dataRootPath = dataRootPath;
        var userEditorRoot = Path.Combine(SettingsStorage.GetDefaultUserDirectory(), "editor");
        _versionsDir = Path.Combine(userEditorRoot, "versions");
        _exportDir = Path.Combine(userEditorRoot, "exports");

        var freshDb = ModdingDataLoader.Load(dataRootPath);
        _atlas = new EditableWorldAtlas(freshDb);
        _baselineSnapshot = AtlasVersioning.CaptureSnapshot(_atlas, "Session Baseline");
        _cmdStack.Clear();

        _selectedProvinceId = null;
        _selectedStateId = null;
        _pendingSplitStart = null;
        _pendingMergeSourceProvId = null;
        _pendingStraitSourceProvId = null;
        _pendingFeaturePoints.Clear();
        _draggingArcId = -1;
        _draggingPointIdx = -1;

        EnsureWorldOverlaysAttached();
        _mapView.EditorInputMode = true;
        _mapView.Visible = true;
        _mapView.RebuildFromAtlas(_atlas);

        PopulateNationPickers();
        RefreshVersionsList();
        UpdateToolbarState();
        Visible = true;
        IsOpen = true;
        SetStatus(_loc.Get(activeLang, "editor.status_ready"));
    }

    public void CloseEditor()
    {
        _mapView.EditorInputMode = false;
        _mapView.Visible = false;
        if (_gizmoCanvas is not null)
            _gizmoCanvas.Visible = false;
        if (_referenceOverlayRect is not null)
            _referenceOverlayRect.Visible = false;
        Visible = false;
        IsOpen = false;
        ExitEditorRequested?.Invoke();
    }

    private void EnsureWorldOverlaysAttached()
    {
        if (_referenceOverlayRect is null)
        {
            _referenceOverlayRect = new TextureRect
            {
                Position = Vector2.Zero,
                Size = MapView2D.WorldCanvasSize,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = new Color(1f, 1f, 1f, 0.35f),
                Visible = false
            };
            _mapView.AddChild(_referenceOverlayRect);
        }

        if (_gizmoCanvas is null)
        {
            _gizmoCanvas = new EditorGizmoCanvas(this);
            _mapView.AddChild(_gizmoCanvas);
        }

        _gizmoCanvas.Visible = true;
        _gizmoCanvas.QueueRedraw();
    }

    private void BuildUi()
    {
        // Top Command Bar
        var topBar = new PanelContainer { AnchorLeft = 0, AnchorRight = 1, AnchorTop = 0, OffsetBottom = 46 };
        var topHBox = new HBoxContainer();
        topHBox.AddThemeConstantOverride("separation", 6);
        topBar.AddChild(topHBox);

        var exitBtn = new Button { Text = "← Menu Principal" };
        exitBtn.Pressed += CloseEditor;
        topHBox.AddChild(exitBtn);

        _undoBtn = new Button { Text = "Desfazer (0)" };
        _undoBtn.Pressed += ExecuteUndo;
        topHBox.AddChild(_undoBtn);

        _redoBtn = new Button { Text = "Refazer (0)" };
        _redoBtn.Pressed += ExecuteRedo;
        topHBox.AddChild(_redoBtn);

        var validateBtn = new Button { Text = "Validar Mapa" };
        validateBtn.Pressed += () => RunValidation(showOkMessage: true);
        topHBox.AddChild(validateBtn);

        var saveWorldBtn = new Button { Text = "Salvar em data/base/" };
        saveWorldBtn.Pressed += SaveEditedWorldToDisk;
        topHBox.AddChild(saveWorldBtn);

        var snapshotBtn = new Button { Text = "Salvar Versão" };
        snapshotBtn.Pressed += SaveNamedVersion;
        topHBox.AddChild(snapshotBtn);

        var onionCheck = new CheckBox { Text = "Onion-Skin Diff" };
        onionCheck.Toggled += pressed =>
        {
            _showOnionSkinDiff = pressed;
            _gizmoCanvas?.QueueRedraw();
        };
        topHBox.AddChild(onionCheck);

        var exportBtn = new Button { Text = "Exportar PNG + _delta.json (F12)" };
        exportBtn.Pressed += ExportHighResAndDelta;
        topHBox.AddChild(exportBtn);

        _statusBanner = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        topHBox.AddChild(_statusBanner);
        AddChild(topBar);

        // Left Tool & Layer Dock
        var leftPanel = new PanelContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorBottom = 1,
            OffsetTop = 52, OffsetLeft = 8, OffsetRight = 228, OffsetBottom = -12
        };
        var leftVBox = new VBoxContainer();
        leftVBox.AddThemeConstantOverride("separation", 5);
        leftPanel.AddChild(leftVBox);

        leftVBox.AddChild(new Label { Text = "FERRAMENTAS DE EDIÇÃO" });
        AddToolButton(leftVBox, "Selecionar & Inspecionar", EditorToolMode.SelectInspect);
        AddToolButton(leftVBox, "Mover Vértice Fronteira", EditorToolMode.EditBorderVertex);
        AddToolButton(leftVBox, "Dividir Província (2 cliques)", EditorToolMode.SplitProvince);
        AddToolButton(leftVBox, "Pintar Dono / Unificar", EditorToolMode.PaintOwnerOrMerge);
        AddToolButton(leftVBox, "Rios / Montanhas / Estreitos", EditorToolMode.DrawGeographyFeature);
        AddToolButton(leftVBox, "Pinos IA (_delta.json)", EditorToolMode.DropAiPin);

        leftVBox.AddChild(new HSeparator());
        leftVBox.AddChild(new Label { Text = "PINTURA / SUBFERRAMENTA" });
        _paintNationPicker = new OptionButton();
        _paintNationPicker.ItemSelected += idx =>
        {
            if (_atlas is not null && idx >= 0 && idx < _atlas.Nations.Count)
                _paintNationId = _atlas.Nations[(int)idx].Id;
        };
        leftVBox.AddChild(_paintNationPicker);

        var mergeBtn = new Button { Text = "Unificar com Próx. Clique" };
        mergeBtn.Pressed += () =>
        {
            if (_selectedProvinceId is null) { SetStatus("Selecione uma província primeiro."); return; }
            _pendingMergeSourceProvId = _selectedProvinceId;
            _activeTool = EditorToolMode.PaintOwnerOrMerge;
            SetStatus($"Clique na província vizinha a absorver dentro de '{_selectedProvinceId}'.");
        };
        leftVBox.AddChild(mergeBtn);

        _featureKindPicker = new OptionButton();
        _featureKindPicker.AddItem("Rio (Spline 4 pts)");
        _featureKindPicker.AddItem("Cordilheira (2+ pts)");
        _featureKindPicker.AddItem("Estreito (2 províncias)");
        _featureKindPicker.ItemSelected += idx =>
        {
            _pendingFeaturePoints.Clear();
            _pendingStraitSourceProvId = null;
            _pendingFeatureKind = idx switch { 1 => "mountain", 2 => "strait", _ => "river" };
        };
        leftVBox.AddChild(_featureKindPicker);

        var finishFeatBtn = new Button { Text = "Concluir Traçado Geográfico" };
        finishFeatBtn.Pressed += CommitPendingGeographyFeature;
        leftVBox.AddChild(finishFeatBtn);

        leftVBox.AddChild(new HSeparator());
        leftVBox.AddChild(new Label { Text = "CAMADAS DO RENDERIZADOR" });
        AddLayerToggle(leftVBox, "Relevo / Terreno", true, v => _mapView.Layers.ShowTerrainRelief = v);
        AddLayerToggle(leftVBox, "Fronteiras Vetoriais", true, v => _mapView.Layers.ShowBorders = v);
        AddLayerToggle(leftVBox, "Divisas de Província", true, v => _mapView.Layers.ShowProvinceBorders = v);
        AddLayerToggle(leftVBox, "Rios & Estreitos", true, v => _mapView.Layers.ShowRivers = v);
        AddLayerToggle(leftVBox, "Rotas & Estradas", true, v => _mapView.Layers.ShowRoads = v);
        AddLayerToggle(leftVBox, "Holdings & Cidades", true, v => _mapView.Layers.ShowHoldings = v);
        AddLayerToggle(leftVBox, "Rótulos Geopolíticos", true, v => _mapView.Layers.ShowLabels = v);

        AddChild(leftPanel);

        // Right Inspector, Reference Overlay & Validation Dock
        var rightPanel = new PanelContainer
        {
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 0, AnchorBottom = 1,
            OffsetTop = 52, OffsetLeft = -348, OffsetRight = -8, OffsetBottom = -12,
            ClipContents = true
        };
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        var rightVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(316, 0)
        };
        rightVBox.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(rightVBox);
        rightPanel.AddChild(scroll);

        _selectionHeader = new Label { Text = "Nenhuma província selecionada", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        rightVBox.AddChild(_selectionHeader);

        _hierarchyMetaLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        rightVBox.AddChild(_hierarchyMetaLabel);

        rightVBox.AddChild(new Label { Text = "Nome da Província:" });
        _provNameEdit = new LineEdit();
        rightVBox.AddChild(_provNameEdit);

        rightVBox.AddChild(new Label { Text = "Nome do Estado / Área:" });
        _stateNameEdit = new LineEdit();
        rightVBox.AddChild(_stateNameEdit);

        rightVBox.AddChild(new Label { Text = "Soberano (Tag):" });
        _ownerAssignPicker = new OptionButton();
        rightVBox.AddChild(_ownerAssignPicker);

        rightVBox.AddChild(new Label { Text = "Bioma / Terreno:" });
        _terrainPicker = new OptionButton();
        foreach (var t in new[] { "plains", "farmlands", "forest", "hills", "mountains", "wetlands", "desert", "coastal" })
            _terrainPicker.AddItem(t);
        rightVBox.AddChild(_terrainPicker);

        rightVBox.AddChild(new Label { Text = "Hub / Holding:" });
        _hubPicker = new OptionButton();
        foreach (var h in new[] { "castle", "city", "temple", "farm", "mine", "wood", "port" })
            _hubPicker.AddItem(h);
        rightVBox.AddChild(_hubPicker);

        var applyPropBtn = new Button { Text = "Aplicar Alterações da Província" };
        applyPropBtn.Pressed += ApplyInspectorEdits;
        rightVBox.AddChild(applyPropBtn);

        var makeCapBtn = new Button { Text = "Definir como Capital Nacional" };
        makeCapBtn.Pressed += () =>
        {
            if (_atlas is null || _selectedProvinceId is null) return;
            ExecuteCommand(new SetCapitalProvinceCommand(_selectedProvinceId));
        };
        rightVBox.AddChild(makeCapBtn);

        var createProvBtn = new Button { Text = "+ Nova Província Adjacente" };
        createProvBtn.Pressed += CreateChildProvinceNextToSelected;
        rightVBox.AddChild(createProvBtn);

        rightVBox.AddChild(new HSeparator());
        rightVBox.AddChild(new Label { Text = "IMAGEM DE REFERÊNCIA (OVERLAY)" });
        var refRow = new HBoxContainer();
        _refPathEdit = new LineEdit { PlaceholderText = "/caminho/mapa_ref.png", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        refRow.AddChild(_refPathEdit);
        var loadRefBtn = new Button { Text = "Abrir" };
        loadRefBtn.Pressed += () => _refFileDialog.PopupCentered(new Vector2I(680, 440));
        refRow.AddChild(loadRefBtn);
        rightVBox.AddChild(refRow);

        _refOpacitySlider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = 0.35 };
        _refOpacitySlider.ValueChanged += val =>
        {
            if (_referenceOverlayRect is not null)
                _referenceOverlayRect.Modulate = new Color(1f, 1f, 1f, (float)val);
        };
        rightVBox.AddChild(_refOpacitySlider);

        rightVBox.AddChild(new HSeparator());
        rightVBox.AddChild(new Label { Text = "HISTÓRICO DE VERSÕES" });
        _versionListPicker = new OptionButton();
        rightVBox.AddChild(_versionListPicker);
        var restoreBtn = new Button { Text = "Restaurar Versão Selecionada" };
        restoreBtn.Pressed += RestoreSelectedVersion;
        rightVBox.AddChild(restoreBtn);

        rightVBox.AddChild(new HSeparator());
        rightVBox.AddChild(new Label { Text = "RELATÓRIO DE VALIDAÇÃO" });
        _validationLogLabel = new Label
        {
            Text = "Clique em 'Validar Mapa' antes de salvar.",
            AutowrapMode = TextServer.AutowrapMode.Word
        };
        rightVBox.AddChild(_validationLogLabel);

        AddChild(rightPanel);

        _refFileDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.png, *.jpg, *.jpeg ; Imagens"]
        };
        _refFileDialog.FileSelected += LoadReferenceOverlayImage;
        AddChild(_refFileDialog);
    }

    private void AddToolButton(VBoxContainer parent, string text, EditorToolMode mode)
    {
        var btn = new Button { Text = text };
        btn.Pressed += () =>
        {
            _activeTool = mode;
            _pendingSplitStart = null;
            _pendingMergeSourceProvId = null;
            _pendingStraitSourceProvId = null;
            _gizmoCanvas?.QueueRedraw();
            SetStatus($"Modo ativo: {text}");
        };
        parent.AddChild(btn);
    }

    private void AddLayerToggle(VBoxContainer parent, string text, bool initial, Action<bool> setter)
    {
        var cb = new CheckBox { Text = text, ButtonPressed = initial };
        cb.Toggled += val =>
        {
            setter(val);
            _mapView.ApplyLayerVisibility();
        };
        parent.AddChild(cb);
    }

    public void HandleCanvasClick(Vector2 worldPos, MouseButton button, bool pressed)
    {
        if (!IsOpen || _atlas is null || button != MouseButton.Left)
            return;

        var nx = Math.Clamp(worldPos.X / MapView2D.WorldCanvasSize.X, 0.005f, 0.995f);
        var ny = Math.Clamp(worldPos.Y / MapView2D.WorldCanvasSize.Y, 0.005f, 0.995f);

        if (_activeTool == EditorToolMode.EditBorderVertex)
        {
            if (pressed)
            {
                TryBeginVertexDrag(nx, ny);
            }
            else if (_draggingArcId >= 0)
            {
                ExecuteCommand(new MoveSharedArcVertexCommand(_draggingArcId, _draggingPointIdx, new Point2D(nx, ny)));
                _draggingArcId = -1;
                _draggingPointIdx = -1;
            }
            return;
        }

        if (!pressed)
            return;

        switch (_activeTool)
        {
            case EditorToolMode.SelectInspect:
                _mapView.TryClickWorldPosition(worldPos);
                SelectProvinceInInspector(_mapView.SelectedProvinceId, _mapView.SelectedStateId);
                break;

            case EditorToolMode.SplitProvince:
                HandleSplitClick(new Point2D(nx, ny), worldPos);
                break;

            case EditorToolMode.PaintOwnerOrMerge:
                HandlePaintOrMergeClick(worldPos);
                break;

            case EditorToolMode.DrawGeographyFeature:
                HandleGeographyFeatureClick(new Point2D(nx, ny), worldPos);
                break;

            case EditorToolMode.DropAiPin:
                var pin = new EditorPinMarker(
                    $"pin_{_atlas.Pins.Count + 1}",
                    nx, ny,
                    "border_review",
                    $"Pin #{_atlas.Pins.Count + 1} @ ({nx:F2}, {ny:F2})");
                ExecuteCommand(new AddEditorPinCommand(pin));
                break;
        }
    }

    public void HandleCanvasDrag(Vector2 worldPos)
    {
        if (_activeTool != EditorToolMode.EditBorderVertex || _draggingArcId < 0 || _atlas is null)
            return;
        _gizmoCanvas?.SetLiveDragPreview(new Vector2(
            Math.Clamp(worldPos.X, 4f, MapView2D.WorldCanvasSize.X - 4f),
            Math.Clamp(worldPos.Y, 4f, MapView2D.WorldCanvasSize.Y - 4f)));
    }

    private void TryBeginVertexDrag(float nx, float ny)
    {
        if (_atlas is null)
            return;

        var bestDist = 0.024f;
        var bestArc = -1;
        var bestIdx = -1;

        foreach (var arc in _atlas.Topology.Arcs)
        {
            for (var i = 0; i < arc.Points.Length; i++)
            {
                var dx = arc.Points[i].X - nx;
                var dy = arc.Points[i].Y - ny;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestArc = arc.Id;
                    bestIdx = i;
                }
            }
        }

        _draggingArcId = bestArc;
        _draggingPointIdx = bestIdx;
        if (bestArc >= 0)
            SetStatus($"Arrastando vértice compartilhado (Arc #{bestArc}, ponto {bestIdx}). Solte para confirmar.");
    }

    private void HandleSplitClick(Point2D pt, Vector2 worldPos)
    {
        if (_atlas is null)
            return;

        if (_pendingSplitStart is null)
        {
            _mapView.TryClickWorldPosition(worldPos);
            _selectedProvinceId = _mapView.SelectedProvinceId;
            _pendingSplitStart = pt;
            SetStatus("Ponto A do corte marcado. Clique no Ponto B para dividir a província.");
            _gizmoCanvas?.QueueRedraw();
            return;
        }

        if (_selectedProvinceId is not null)
        {
            var newId = $"{_selectedProvinceId}_split_{_atlas.NextPersistentUid}";
            var newName = $"{_selectedProvinceId} Setor {_atlas.NextPersistentUid}";
            ExecuteCommand(new SplitProvinceCommand(_selectedProvinceId, _pendingSplitStart.Value, pt, newId, newName));
        }

        _pendingSplitStart = null;
        _gizmoCanvas?.QueueRedraw();
    }

    private void HandlePaintOrMergeClick(Vector2 worldPos)
    {
        if (_atlas is null || !_mapView.TryClickWorldPosition(worldPos))
            return;

        var provId = _mapView.SelectedProvinceId;
        var stateId = _mapView.SelectedStateId;
        if (provId is null)
            return;

        if (_pendingMergeSourceProvId is not null && _pendingMergeSourceProvId != provId)
        {
            ExecuteCommand(new MergeProvincesCommand(_pendingMergeSourceProvId, provId));
            _pendingMergeSourceProvId = null;
            return;
        }

        if (stateId is not null)
        {
            ExecuteCommand(new TransferTerritoryCommand(provId, _paintNationId));
            SelectProvinceInInspector(provId, stateId);
        }
    }

    private void HandleGeographyFeatureClick(Point2D pt, Vector2 worldPos)
    {
        if (_atlas is null)
            return;

        if (_pendingFeatureKind == "strait")
        {
            if (!_mapView.TryClickWorldPosition(worldPos) || _mapView.SelectedProvinceId is null)
                return;
            if (_pendingStraitSourceProvId is null)
            {
                _pendingStraitSourceProvId = _mapView.SelectedProvinceId;
                SetStatus($"Estreito: origem '{_pendingStraitSourceProvId}'. Clique na província oposta.");
            }
            else if (_pendingStraitSourceProvId != _mapView.SelectedProvinceId)
            {
                var pA = _pendingStraitSourceProvId;
                var pB = _mapView.SelectedProvinceId;
                ExecuteCommand(new EditGeographyFeatureCommand($"Add Strait {pA} <-> {pB}", f =>
                {
                    var list = f.Straits.ToList();
                    list.Add(new StraitCrossing($"strait_{list.Count + 1}", $"Estreito {pA}-{pB}", pA, pB));
                    return f with { Straits = list.ToArray() };
                }));
                _pendingStraitSourceProvId = null;
            }
            return;
        }

        _pendingFeaturePoints.Add(pt);
        SetStatus($"Traçando {_pendingFeatureKind}: {_pendingFeaturePoints.Count} pontos marcados. Clique em 'Concluir Traçado Geográfico'.");
        _gizmoCanvas?.QueueRedraw();
    }

    private void CommitPendingGeographyFeature()
    {
        if (_atlas is null || _pendingFeaturePoints.Count < 2)
        {
            SetStatus("Marque ao menos 2 pontos no mapa antes de concluir o traçado.");
            return;
        }

        var pts = _pendingFeaturePoints.ToArray();
        _pendingFeaturePoints.Clear();

        if (_pendingFeatureKind == "mountain")
        {
            ExecuteCommand(new EditGeographyFeatureCommand("Add Mountain Range", f =>
            {
                var list = f.MountainRanges.ToList();
                list.Add(new MountainRangeFeature($"mtn_{list.Count + 1}", $"Cordilheira {list.Count + 1}", pts));
                return f with { MountainRanges = list.ToArray() };
            }));
        }
        else
        {
            ExecuteCommand(new EditGeographyFeatureCommand("Add River", f =>
            {
                var list = f.Rivers.ToList();
                list.Add(new RiverFeature($"riv_{list.Count + 1}", $"Rio {list.Count + 1}", pts));
                return f with { Rivers = list.ToArray() };
            }));
        }
    }

    private void SelectProvinceInInspector(string? provId, string? stateId)
    {
        _selectedProvinceId = provId;
        _selectedStateId = stateId;
        if (_atlas is null || provId is null || stateId is null)
            return;

        var st = _atlas.States.FirstOrDefault(s => s.Id == stateId);
        var prov = st?.EnsureProvinceDetails().FirstOrDefault(p => p.Id == provId);
        if (st is null || prov is null)
            return;

        var uid = _atlas.AllocatePersistentUid(prov.Id);
        _selectionHeader.Text = $"{prov.Name} ({prov.Id}) [UID #{uid}]";
        _provNameEdit.Text = prov.Name;
        _stateNameEdit.Text = st.Name;

        var srName = _atlas.Hierarchy.SuperRegions.FirstOrDefault(s => s.Id == st.SuperRegionId)?.DefaultName ?? st.MacroRegion;
        var rName = _atlas.Hierarchy.Regions.FirstOrDefault(r => r.Id == st.RegionId)?.DefaultName ?? "-";
        _hierarchyMetaLabel.Text = $"Super-Região: {srName} | Região: {rName} | Litoral: {prov.IsCoastal} | Adjacências: {string.Join(", ", st.AdjacentStateIds.Take(5))}";

        var nIdx = _atlas.Nations.FindIndex(n => n.Id == st.OwnerNationId);
        if (nIdx >= 0)
            _ownerAssignPicker.Selected = nIdx;

        SelectOptionText(_terrainPicker, prov.Terrain);
        SelectOptionText(_hubPicker, prov.HubType);
    }

    private void ApplyInspectorEdits()
    {
        if (_atlas is null || _selectedProvinceId is null || _selectedStateId is null)
            return;

        if (!string.IsNullOrWhiteSpace(_provNameEdit.Text))
            ExecuteCommand(new RenameTerritoryCommand(RenameTerritoryCommand.TargetKind.Province, _selectedProvinceId, _provNameEdit.Text.Trim()));

        if (!string.IsNullOrWhiteSpace(_stateNameEdit.Text))
            ExecuteCommand(new RenameTerritoryCommand(RenameTerritoryCommand.TargetKind.State, _selectedStateId, _stateNameEdit.Text.Trim()));

        var targetNationIdx = _ownerAssignPicker.Selected;
        if (targetNationIdx >= 0 && targetNationIdx < _atlas.Nations.Count)
            ExecuteCommand(new TransferTerritoryCommand(_selectedStateId, _atlas.Nations[targetNationIdx].Id));
    }

    private void CreateChildProvinceNextToSelected()
    {
        if (_atlas is null || _selectedStateId is null || _selectedProvinceId is null)
            return;

        var st = _atlas.States.FirstOrDefault(s => s.Id == _selectedStateId);
        var baseProv = st?.EnsureProvinceDetails().FirstOrDefault(p => p.Id == _selectedProvinceId);
        if (baseProv is null)
            return;

        var id = $"{_selectedStateId}_new_{_atlas.NextPersistentUid}";
        var newProv = new ProvinceDefinition(
            id,
            $"Nova {baseProv.Name}",
            baseProv.Terrain,
            "farm",
            false,
            baseProv.IsCoastal,
            Math.Clamp(baseProv.AnchorX + 0.025, 0.04, 0.96),
            Math.Clamp(baseProv.AnchorY + 0.015, 0.05, 0.94));
        ExecuteCommand(new CreateProvinceCommand(_selectedStateId, newProv));
    }

    private void ExecuteCommand(IAtlasCommand cmd)
    {
        if (_atlas is null)
            return;

        _cmdStack.Execute(_atlas, cmd);
        _mapView.RebuildFromAtlas(_atlas);
        UpdateToolbarState();
        SetStatus($"Executado: {cmd.Description}");
        _gizmoCanvas?.QueueRedraw();
    }

    private void ExecuteUndo()
    {
        if (_atlas is null || !_cmdStack.Undo(_atlas))
            return;
        _mapView.RebuildFromAtlas(_atlas);
        UpdateToolbarState();
        SetStatus("Desfeito.");
        _gizmoCanvas?.QueueRedraw();
    }

    private void ExecuteRedo()
    {
        if (_atlas is null || !_cmdStack.Redo(_atlas))
            return;
        _mapView.RebuildFromAtlas(_atlas);
        UpdateToolbarState();
        SetStatus("Refeito.");
        _gizmoCanvas?.QueueRedraw();
    }

    private bool RunValidation(bool showOkMessage)
    {
        if (_atlas is null)
            return false;

        var report = AtlasValidator.Validate(_atlas);
        var lines = report.Issues.Take(10).Select(i => $"[{i.Severity}] {i.Code}: {i.Message}");
        _validationLogLabel.Text = report.Issues.Count == 0
            ? "OK! Topologia planar, fronteiras simétricas e litorais 100% válidos."
            : $"Erros: {report.ErrorCount} | Avisos: {report.WarningCount}\n" + string.Join('\n', lines);

        if (!report.IsValid)
            SetStatus($"Validação bloqueou salvamento ({report.ErrorCount} erro(s)).");
        else if (showOkMessage)
            SetStatus($"Validação concluída: {report.ErrorCount} erros, {report.WarningCount} avisos.");

        return report.IsValid;
    }

    private void SaveEditedWorldToDisk()
    {
        if (_atlas is null || !RunValidation(showOkMessage: false))
            return;

        var baseDir = Path.Combine(_dataRootPath, "base");
        AtlasVersioning.SaveAtlasToDataRoot(_atlas, baseDir);
        SaveNamedVersion();
        WorldDataSavedRequested?.Invoke();
        SetStatus($"Dados persistidos com segurança em {baseDir}!");
    }

    private void SaveNamedVersion()
    {
        if (_atlas is null)
            return;
        var snap = AtlasVersioning.CaptureSnapshot(_atlas, $"Edit_{DateTime.Now:HH:mm:ss}");
        AtlasVersioning.SaveVersionFile(_versionsDir, snap);
        RefreshVersionsList();
        SetStatus($"Snapshot '{snap.VersionId}' salvo em histórico.");
    }

    private void RestoreSelectedVersion()
    {
        if (_atlas is null)
            return;
        var versions = AtlasVersioning.ListSavedVersions(_versionsDir);
        var idx = _versionListPicker.Selected;
        if (idx < 0 || idx >= versions.Count)
            return;

        AtlasVersioning.RestoreSnapshot(_atlas, versions[idx]);
        _cmdStack.Clear();
        _mapView.RebuildFromAtlas(_atlas);
        UpdateToolbarState();
        SetStatus($"Versão '{versions[idx].Label}' restaurada!");
    }

    private void RefreshVersionsList()
    {
        _versionListPicker.Clear();
        foreach (var v in AtlasVersioning.ListSavedVersions(_versionsDir))
            _versionListPicker.AddItem($"{v.Label} ({v.States.Length} áreas)");
    }

    private void PopulateNationPickers()
    {
        _paintNationPicker.Clear();
        _ownerAssignPicker.Clear();
        if (_atlas is null)
            return;
        foreach (var n in _atlas.Nations)
        {
            var label = $"{n.Id} — {n.Name}";
            _paintNationPicker.AddItem(label);
            _ownerAssignPicker.AddItem(label);
        }
    }

    private void LoadReferenceOverlayImage(string path)
    {
        _refPathEdit.Text = path;
        if (_referenceOverlayRect is null || !File.Exists(path))
            return;
        var img = new Image();
        if (img.Load(path) == Error.Ok)
        {
            _referenceOverlayRect.Texture = ImageTexture.CreateFromImage(img);
            _referenceOverlayRect.Modulate = new Color(1f, 1f, 1f, (float)_refOpacitySlider.Value);
            _referenceOverlayRect.Visible = true;
            SetStatus($"Overlay de referência carregado: {Path.GetFileName(path)}");
        }
    }

    public string QaExerciseEditorWorkflow()
    {
        if (_atlas is null)
            return "editor_not_open";

        var worldPos = _mapView.GetProvinceWorldPosition("prov_briggston");
        var clicked = _mapView.TryClickWorldPosition(worldPos);
        if (clicked && _mapView.SelectedProvinceId is not null && _mapView.SelectedStateId is not null)
            SelectProvinceInInspector(_mapView.SelectedProvinceId, _mapView.SelectedStateId);

        _activeTool = EditorToolMode.EditBorderVertex;
        var beforeRename = _atlas.States.First(s => s.Id == "state_aethelgard_heart").Name;
        ExecuteCommand(new RenameTerritoryCommand(RenameTerritoryCommand.TargetKind.State, "state_aethelgard_heart", "Bacia QA"));
        var renamed = _atlas.States.First(s => s.Id == "state_aethelgard_heart").Name;
        ExecuteUndo();
        var afterUndo = _atlas.States.First(s => s.Id == "state_aethelgard_heart").Name;
        var valid = RunValidation(showOkMessage: true);
        _gizmoCanvas?.QueueRedraw();

        return $"clicked={clicked} sel={_selectedProvinceId} renamed={renamed} undo={afterUndo == beforeRename} valid={valid} verts={_atlas.Topology.Vertices.Count}";
    }

    public void ExportHighResAndDelta()
    {
        if (_atlas is null || _baselineSnapshot is null)
            return;

        Directory.CreateDirectory(_exportDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var deltaPath = Path.Combine(_exportDir, $"voracity_edit_{stamp}_delta.json");
        var delta = AtlasVersioning.ComputeDelta(_baselineSnapshot, _atlas);
        AtlasVersioning.SaveDeltaJson(deltaPath, delta);

        if (DisplayServer.GetName() != "headless" && GetViewport()?.GetTexture()?.GetImage() is { } vpImg)
        {
            var pngPath = Path.Combine(_exportDir, $"voracity_edit_{stamp}.png");
            vpImg.SavePng(pngPath);
            SetStatus($"Exportado PNG + _delta.json em {_exportDir}");
        }
        else
        {
            SetStatus($"Exportado _delta.json em {deltaPath}");
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsOpen)
            return;

        if (@event is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.F12)
                ExportHighResAndDelta();
            else if (key.CtrlPressed && key.Keycode == Key.Z)
                ExecuteUndo();
            else if (key.CtrlPressed && key.Keycode == Key.Y)
                ExecuteRedo();
        }
    }

    private void UpdateToolbarState()
    {
        _undoBtn.Text = $"Desfazer ({_cmdStack.UndoCount})";
        _undoBtn.Disabled = _cmdStack.UndoCount == 0;
        _redoBtn.Text = $"Refazer ({_cmdStack.RedoCount})";
        _redoBtn.Disabled = _cmdStack.RedoCount == 0;
    }

    private void SetStatus(string msg) => _statusBanner.Text = msg;

    private static void SelectOptionText(OptionButton btn, string text)
    {
        for (var i = 0; i < btn.ItemCount; i++)
        {
            if (string.Equals(btn.GetItemText(i), text, StringComparison.OrdinalIgnoreCase))
            {
                btn.Selected = i;
                return;
            }
        }
    }

    private sealed partial class EditorGizmoCanvas : Node2D
    {
        private readonly MapEditorView _editor;
        private Vector2? _dragPreview;

        public EditorGizmoCanvas(MapEditorView editor) => _editor = editor;

        public void SetLiveDragPreview(Vector2 worldPos)
        {
            _dragPreview = worldPos;
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (!_editor.IsOpen || _editor._atlas is null)
                return;

            var atlas = _editor._atlas;

            // Show editable shared vertices when Vertex tool is active
            if (_editor._activeTool == EditorToolMode.EditBorderVertex)
            {
                foreach (var v in atlas.Topology.Vertices)
                {
                    var pos = new Vector2(v.X * MapView2D.WorldCanvasSize.X, v.Y * MapView2D.WorldCanvasSize.Y);
                    DrawCircle(pos, 3.2f, new Color(0.98f, 0.85f, 0.30f, 0.88f));
                }

                if (_dragPreview.HasValue)
                    DrawCircle(_dragPreview.Value, 6.0f, new Color(0.25f, 0.95f, 0.45f, 0.95f));
            }

            // Show Onion-Skin Diff baseline vertices if enabled
            if (_editor._showOnionSkinDiff && _editor._baselineSnapshot is not null)
            {
                foreach (var st in _editor._baselineSnapshot.States)
                {
                    var pos = new Vector2((float)st.MapX * MapView2D.WorldCanvasSize.X, (float)st.MapY * MapView2D.WorldCanvasSize.Y);
                    DrawArc(pos, 18f, 0, Mathf.Tau, 18, new Color(0.95f, 0.35f, 0.65f, 0.55f), 1.6f);
                }
            }

            // Show split line start point
            if (_editor._pendingSplitStart is { } splitPt)
            {
                var p = new Vector2(splitPt.X * MapView2D.WorldCanvasSize.X, splitPt.Y * MapView2D.WorldCanvasSize.Y);
                DrawCircle(p, 5.5f, new Color(1.0f, 0.28f, 0.28f, 0.95f));
            }

            // Show pending geography spline
            if (_editor._pendingFeaturePoints.Count > 0)
            {
                var pts = new Vector2[_editor._pendingFeaturePoints.Count];
                for (var i = 0; i < pts.Length; i++)
                {
                    pts[i] = new Vector2(_editor._pendingFeaturePoints[i].X * MapView2D.WorldCanvasSize.X, _editor._pendingFeaturePoints[i].Y * MapView2D.WorldCanvasSize.Y);
                    DrawCircle(pts[i], 4.5f, new Color(0.28f, 0.88f, 0.98f, 0.95f));
                }
                if (pts.Length >= 2)
                    DrawPolyline(pts, new Color(0.28f, 0.88f, 0.98f, 0.80f), 2.5f);
            }

            // Show AI feedback pins
            foreach (var pin in atlas.Pins)
            {
                var pos = new Vector2(pin.X * MapView2D.WorldCanvasSize.X, pin.Y * MapView2D.WorldCanvasSize.Y);
                DrawCircle(pos, 6f, new Color(0.99f, 0.50f, 0.14f, 0.92f));
                DrawArc(pos, 8f, 0, Mathf.Tau, 16, Colors.White, 1.4f);
            }
        }
    }
}
