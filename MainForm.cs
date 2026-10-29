using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using BlockDiagram.Controls;
using BlockDiagram.Enums;
using BlockDiagram.Models;

namespace BlockDiagram;

public sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<Shape> _shapes = new();
    private readonly CanvasPanel _canvas = new();
    private readonly PropertyGrid _propertyGrid = new();
    private readonly List<(ToolStripButton Button, ToolMode Mode)> _toolButtons = new();
    private readonly ToolStripStatusLabel _modeStatus = new("Инструмент: Выбор");
    private readonly ToolStripStatusLabel _coordinatesStatus = new("Координаты: X=0, Y=0");
    private readonly ToolStripStatusLabel _countStatus = new("Элементов: 0");

    private ToolMode _currentMode = ToolMode.Select;
    private Shape? _selectedShape;
    private Point _dragStartPoint;
    private bool _isDragging;
    private bool _isResizing;
    private ResizeHandle _activeResizeHandle;
    private Rectangle _resizeOriginalBounds;
    private Shape? _connectionSource;
    private bool _isConnecting;
    private Point _connectionPreviewEnd;
    private Color _currentStrokeColor = Color.FromArgb(35, 42, 52);
    private Color _currentFillColor = Color.FromArgb(225, 239, 255);
    private float _currentStrokeWidth = 2f;

    public MainForm()
    {
        Text = "BlockDiagram — редактор блок-схем";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1360, 820);
        MinimumSize = new Size(960, 620);
        KeyPreview = true;
        Font = new Font("Segoe UI", 9f);

        MenuStrip menuStrip = BuildMenuStrip();
        ToolStrip toolStrip = BuildToolStrip();
        StatusStrip statusStrip = BuildStatusStrip();

        var splitContainer = new SplitContainer
        {
            Size = new Size(1300, 700),
            Orientation = Orientation.Vertical,
            SplitterDistance = 950,
            Dock = DockStyle.Fill,
            Panel1MinSize = 400,
            Panel2MinSize = 250,
            SplitterWidth = 6
        };

        _canvas.Dock = DockStyle.Fill;
        _canvas.Paint += Canvas_Paint;
        _canvas.MouseDown += Canvas_MouseDown;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.MouseUp += Canvas_MouseUp;
        splitContainer.Panel1.Controls.Add(_canvas);

        _propertyGrid.Dock = DockStyle.Fill;
        _propertyGrid.ToolbarVisible = true;
        _propertyGrid.HelpVisible = true;
        _propertyGrid.PropertySort = PropertySort.Categorized;
        _propertyGrid.PropertyValueChanged += (_, _) =>
        {
            UpdateConnections();
            _canvas.Invalidate();
        };
        splitContainer.Panel2.Controls.Add(_propertyGrid);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(menuStrip, 0, 0);
        layout.Controls.Add(toolStrip, 0, 1);
        layout.Controls.Add(splitContainer, 0, 2);
        layout.Controls.Add(statusStrip, 0, 3);
        Controls.Add(layout);
        MainMenuStrip = menuStrip;

        Shown += (_, _) =>
        {
            if (splitContainer.Width > splitContainer.Panel2MinSize + 450)
            {
                splitContainer.SplitterDistance = Math.Clamp(splitContainer.Width - 290, 450, splitContainer.Width - splitContainer.Panel2MinSize);
            }
        };
        KeyDown += MainForm_KeyDown;
        UpdateStatus();
    }

    private MenuStrip BuildMenuStrip()
    {
        var menu = new MenuStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden };
        var fileMenu = new ToolStripMenuItem("Файл");
        fileMenu.DropDownItems.Add("Сохранить схему в JSON...", null, (_, _) => SaveJson());
        fileMenu.DropDownItems.Add("Загрузить схему из JSON...", null, (_, _) => LoadJson());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("Экспорт в PNG...", null, (_, _) => ExportImage(ImageFormat.Png, "png"));
        fileMenu.DropDownItems.Add("Экспорт в JPG...", null, (_, _) => ExportImage(ImageFormat.Jpeg, "jpg"));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("Выход", null, (_, _) => Close());
        menu.Items.Add(fileMenu);
        return menu;
    }

    private ToolStrip BuildToolStrip()
    {
        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(18, 18),
            Stretch = true
        };

        toolStrip.Items.Add(CreateModeButton("Выбор", ToolMode.Select));
        toolStrip.Items.Add(CreateModeButton("Процесс", ToolMode.Rectangle));
        toolStrip.Items.Add(CreateModeButton("Эллипс", ToolMode.Ellipse));
        toolStrip.Items.Add(CreateModeButton("Условие", ToolMode.Rhombus));
        toolStrip.Items.Add(CreateModeButton("Блок текста", ToolMode.TextBlock));
        toolStrip.Items.Add(CreateModeButton("Начало/конец", ToolMode.Terminator));
        toolStrip.Items.Add(CreateModeButton("Связь-стрелка", ToolMode.Line));
        toolStrip.Items.Add(new ToolStripSeparator());

        var strokeButton = new ToolStripButton("Цвет контура");
        strokeButton.Click += (_, _) => ChooseColor(false);
        toolStrip.Items.Add(strokeButton);

        var fillButton = new ToolStripButton("Заливка");
        fillButton.Click += (_, _) => ChooseColor(true);
        toolStrip.Items.Add(fillButton);

        toolStrip.Items.Add(new ToolStripLabel("Толщина:"));
        var widthCombo = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Width = 55
        };
        widthCombo.Items.AddRange(new object[] { "1", "2", "3", "5", "8" });
        widthCombo.SelectedItem = "2";
        widthCombo.SelectedIndexChanged += (_, _) =>
        {
            if (float.TryParse(widthCombo.SelectedItem?.ToString(), out float width))
            {
                _currentStrokeWidth = width;
                if (_selectedShape is not null)
                {
                    _selectedShape.StrokeWidth = width;
                    _propertyGrid.Refresh();
                    _canvas.Invalidate();
                }
            }
        };
        toolStrip.Items.Add(widthCombo);
        toolStrip.Items.Add(new ToolStripSeparator());

        var duplicateButton = new ToolStripButton("Копия");
        duplicateButton.Click += (_, _) => DuplicateSelected();
        toolStrip.Items.Add(duplicateButton);

        var deleteButton = new ToolStripButton("Удалить");
        deleteButton.Click += (_, _) => DeleteSelected();
        toolStrip.Items.Add(deleteButton);

        var clearButton = new ToolStripButton("Очистить");
        clearButton.Click += (_, _) => ClearCanvas();
        toolStrip.Items.Add(clearButton);

        SetMode(ToolMode.Select);
        return toolStrip;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false };
        strip.Items.Add(_modeStatus);
        strip.Items.Add(new ToolStripStatusLabel { Spring = true });
        strip.Items.Add(_coordinatesStatus);
        strip.Items.Add(new ToolStripStatusLabel(" | "));
        strip.Items.Add(_countStatus);
        return strip;
    }

    private ToolStripButton CreateModeButton(string title, ToolMode mode)
    {
        var button = new ToolStripButton(title) { CheckOnClick = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => SetMode(mode);
        _toolButtons.Add((button, mode));
        return button;
    }

    private void SetMode(ToolMode mode)
    {
        _currentMode = mode;
        foreach ((ToolStripButton button, ToolMode buttonMode) in _toolButtons)
        {
            button.Checked = buttonMode == mode;
        }

        if (mode != ToolMode.Line)
        {
            _isConnecting = false;
            _connectionSource = null;
        }

        _modeStatus.Text = $"Инструмент: {ModeName(mode)}";
        _canvas.Cursor = mode == ToolMode.Line ? Cursors.Cross : Cursors.Default;
        _canvas.Invalidate();
    }

    private static string ModeName(ToolMode mode) => mode switch
    {
        ToolMode.Select => "Выбор",
        ToolMode.Rectangle => "Процесс",
        ToolMode.Ellipse => "Эллипс",
        ToolMode.Rhombus => "Условие",
        ToolMode.TextBlock => "Блок текста",
        ToolMode.Terminator => "Начало/конец",
        ToolMode.Line => "Связь-стрелка",
        _ => "Выбор"
    };

    private void ChooseColor(bool fill)
    {
        using var dialog = new ColorDialog
        {
            FullOpen = true,
            Color = fill ? _currentFillColor : _currentStrokeColor
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (fill)
        {
            _currentFillColor = dialog.Color;
            if (_selectedShape is not null)
            {
                _selectedShape.FillColor = dialog.Color;
            }
        }
        else
        {
            _currentStrokeColor = dialog.Color;
            if (_selectedShape is not null)
            {
                _selectedShape.StrokeColor = dialog.Color;
            }
        }

        _propertyGrid.Refresh();
        _canvas.Invalidate();
    }

    private Shape? CreateShape(Point location)
    {
        Shape? shape = _currentMode switch
        {
            ToolMode.Rectangle => new RectangleShape { Text = "Процесс" },
            ToolMode.Ellipse => new EllipseShape { Text = "Начало / конец" },
            ToolMode.Rhombus => new RhombusShape { Text = "Условие?" },
            ToolMode.TextBlock => new TextBlockShape { Text = "Данные" },
            ToolMode.Terminator => new TerminatorShape { Text = "Начало / конец" },
            _ => null
        };

        if (shape is null)
        {
            return null;
        }

        shape.X = location.X - shape.Width / 2;
        shape.Y = location.Y - shape.Height / 2;
        shape.StrokeColor = _currentStrokeColor;
        shape.FillColor = _currentFillColor;
        shape.StrokeWidth = _currentStrokeWidth;
        return shape;
    }

    private void Canvas_Paint(object? sender, PaintEventArgs e)
    {
        RenderScene(e.Graphics, _canvas.ClientSize, true, true);
    }

    private void RenderScene(Graphics graphics, Size size, bool drawGrid, bool drawConnectionPreview)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.White);

        if (drawGrid)
        {
            DrawGrid(graphics, size);
        }

        UpdateConnections();
        foreach (Shape shape in _shapes.OrderBy(item => item.DrawLayer))
        {
            shape.Draw(graphics);
        }

        if (drawConnectionPreview && _isConnecting && _connectionSource is not null)
        {
            using var arrow = new AdjustableArrowCap(5, 7, true);
            using var pen = new Pen(_currentStrokeColor, _currentStrokeWidth)
            {
                CustomEndCap = arrow,
                EndCap = LineCap.Custom
            };
            Point start = _connectionSource.GetConnectionPoint(_connectionPreviewEnd);
            graphics.DrawLine(pen, start, _connectionPreviewEnd);
        }
    }

    private static void DrawGrid(Graphics graphics, Size size)
    {
        using var pen = new Pen(Color.FromArgb(239, 242, 246), 1f);
        const int spacing = 24;
        for (int x = 0; x < size.Width; x += spacing)
        {
            graphics.DrawLine(pen, x, 0, x, size.Height);
        }

        for (int y = 0; y < size.Height; y += spacing)
        {
            graphics.DrawLine(pen, 0, y, size.Width, y);
        }
    }

    private void Canvas_MouseDown(object? sender, MouseEventArgs e)
    {
        _canvas.Focus();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_currentMode == ToolMode.Line)
        {
            _connectionSource = FindConnectableAt(e.Location);
            if (_connectionSource is not null)
            {
                _connectionPreviewEnd = e.Location;
                _isConnecting = true;
                _canvas.Invalidate();
            }

            return;
        }

        if (_currentMode != ToolMode.Select)
        {
            Shape? created = CreateShape(e.Location);
            if (created is not null)
            {
                _shapes.Add(created);
                SelectShape(created);
                UpdateConnections();
                UpdateStatus();
                _canvas.Invalidate();
            }

            return;
        }

        if (_selectedShape is not null)
        {
            ResizeHandle handle = _selectedShape.GetResizeHandleAt(e.Location);
            if (handle != ResizeHandle.None)
            {
                _activeResizeHandle = handle;
                _resizeOriginalBounds = _selectedShape.Bounds;
                _isResizing = true;
                return;
            }
        }

        Shape? hit = FindShapeAt(e.Location);
        SelectShape(hit);
        if (hit is not null)
        {
            _dragStartPoint = e.Location;
            _isDragging = hit.CanMove;
        }

        UpdateStatus();
        _canvas.Invalidate();
    }

    private void Canvas_MouseMove(object? sender, MouseEventArgs e)
    {
        _coordinatesStatus.Text = $"Координаты: X={e.X}, Y={e.Y}";

        if (_isConnecting && _connectionSource is not null)
        {
            _connectionPreviewEnd = e.Location;
            _canvas.Invalidate();
            return;
        }

        if (_isResizing && _selectedShape is not null)
        {
            _selectedShape.Resize(_activeResizeHandle, _resizeOriginalBounds, e.Location);
            UpdateConnections();
            _propertyGrid.Refresh();
            _canvas.Invalidate();
            return;
        }

        if (_isDragging && _selectedShape is not null)
        {
            int deltaX = e.X - _dragStartPoint.X;
            int deltaY = e.Y - _dragStartPoint.Y;
            _selectedShape.Move(deltaX, deltaY);
            _dragStartPoint = e.Location;
            UpdateConnections();
            _propertyGrid.Refresh();
            _canvas.Invalidate();
            return;
        }

        ResizeHandle hoverHandle = _selectedShape?.GetResizeHandleAt(e.Location) ?? ResizeHandle.None;
        _canvas.Cursor = CursorForHandle(hoverHandle, _currentMode);
    }

    private void Canvas_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_isConnecting && _connectionSource is not null)
        {
            Shape? target = FindConnectableAt(e.Location);
            if (target is not null && target.Id != _connectionSource.Id)
            {
                var line = new ConnectionLineShape
                {
                    SourceShapeId = _connectionSource.Id,
                    TargetShapeId = target.Id,
                    StrokeColor = _currentStrokeColor,
                    StrokeWidth = _currentStrokeWidth
                };
                line.UpdateConnections(_shapes.Append(line).ToList());
                _shapes.Add(line);
            }

            _connectionSource = null;
            _isConnecting = false;
            UpdateConnections();
            UpdateStatus();
            _canvas.Invalidate();
        }

        _isDragging = false;
        _isResizing = false;
        _activeResizeHandle = ResizeHandle.None;
    }

    private static Cursor CursorForHandle(ResizeHandle handle, ToolMode mode)
    {
        if (mode != ToolMode.Select)
        {
            return mode == ToolMode.Line ? Cursors.Cross : Cursors.Default;
        }

        return handle switch
        {
            ResizeHandle.NorthWest or ResizeHandle.SouthEast => Cursors.SizeNWSE,
            ResizeHandle.NorthEast or ResizeHandle.SouthWest => Cursors.SizeNESW,
            ResizeHandle.North or ResizeHandle.South => Cursors.SizeNS,
            ResizeHandle.East or ResizeHandle.West => Cursors.SizeWE,
            _ => Cursors.Default
        };
    }

    private Shape? FindShapeAt(Point point)
    {
        return _shapes
            .OrderByDescending(shape => shape.DrawLayer)
            .ThenByDescending(shape => _shapes.IndexOf(shape))
            .FirstOrDefault(shape => shape.ContainsPoint(point));
    }

    private Shape? FindConnectableAt(Point point)
    {
        return _shapes
            .Where(shape => shape.CanConnect)
            .OrderByDescending(shape => shape.DrawLayer)
            .ThenByDescending(shape => _shapes.IndexOf(shape))
            .FirstOrDefault(shape => shape.ContainsPoint(point));
    }

    private void SelectShape(Shape? shape)
    {
        foreach (Shape item in _shapes)
        {
            item.IsSelected = false;
        }

        _selectedShape = shape;
        if (_selectedShape is not null)
        {
            _selectedShape.IsSelected = true;
        }

        _propertyGrid.SelectedObject = _selectedShape;
        _propertyGrid.Refresh();
    }

    private void UpdateConnections()
    {
        foreach (Shape shape in _shapes)
        {
            shape.UpdateConnections(_shapes);
        }
    }

    private void DuplicateSelected()
    {
        if (_selectedShape is null)
        {
            return;
        }

        Shape duplicate = _selectedShape.Clone();
        duplicate.Move(20, 20);
        _shapes.Add(duplicate);
        SelectShape(duplicate);
        UpdateConnections();
        UpdateStatus();
        _canvas.Invalidate();
    }

    private void DeleteSelected()
    {
        if (_selectedShape is null)
        {
            return;
        }

        Guid id = _selectedShape.Id;
        _shapes.Remove(_selectedShape);
        _shapes.RemoveAll(shape => shape.References(id));
        SelectShape(null);
        UpdateConnections();
        UpdateStatus();
        _canvas.Invalidate();
    }

    private void ClearCanvas()
    {
        if (_shapes.Count == 0)
        {
            return;
        }

        DialogResult answer = MessageBox.Show(this, "Удалить все элементы схемы?", "Очистка холста", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _shapes.Clear();
        SelectShape(null);
        UpdateStatus();
        _canvas.Invalidate();
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.D)
        {
            DuplicateSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _isConnecting = false;
            _connectionSource = null;
            _isDragging = false;
            _isResizing = false;
            _canvas.Invalidate();
        }
    }

    private void SaveJson()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON схема (*.json)|*.json",
            DefaultExt = "json",
            AddExtension = true,
            FileName = "diagram.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(_shapes, JsonOptions);
            File.WriteAllText(dialog.FileName, json);
            MessageBox.Show(this, "Схема сохранена в JSON.", "Сохранение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось сохранить схему:\n{exception.Message}", "Ошибка сохранения", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadJson()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON схема (*.json)|*.json",
            DefaultExt = "json",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            List<Shape>? loaded = JsonSerializer.Deserialize<List<Shape>>(json, JsonOptions);
            if (loaded is null)
            {
                throw new JsonException("Файл не содержит список элементов схемы.");
            }

            _shapes.Clear();
            _shapes.AddRange(loaded);
            foreach (Shape shape in _shapes)
            {
                shape.IsSelected = false;
            }

            SelectShape(null);
            UpdateConnections();
            UpdateStatus();
            _canvas.Invalidate();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось загрузить схему:\n{exception.Message}", "Ошибка загрузки", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportImage(ImageFormat format, string extension)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = $"Изображение {extension.ToUpperInvariant()} (*.{extension})|*.{extension}",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"diagram.{extension}"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        List<Shape> selected = _shapes.Where(shape => shape.IsSelected).ToList();
        try
        {
            foreach (Shape shape in _shapes)
            {
                shape.IsSelected = false;
            }

            using var bitmap = new Bitmap(Math.Max(1, _canvas.ClientSize.Width), Math.Max(1, _canvas.ClientSize.Height));
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                RenderScene(graphics, bitmap.Size, false, false);
            }

            bitmap.Save(dialog.FileName, format);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Не удалось экспортировать изображение:\n{exception.Message}", "Ошибка экспорта", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            foreach (Shape shape in selected)
            {
                shape.IsSelected = true;
            }

            _canvas.Invalidate();
        }
    }

    private void UpdateStatus()
    {
        _modeStatus.Text = $"Инструмент: {ModeName(_currentMode)}";
        _countStatus.Text = $"Элементов: {_shapes.Count}";
    }
}
