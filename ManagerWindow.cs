using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RIR_PluginManager
{
    public enum NextAction { None, Rhino, Grasshopper }

    public sealed class ManagerWindow : Window
    {
        sealed class Row
        {
            public PluginGroup Group;
            public CheckBox Box;
            public Border Container;        // строка списка целиком (для выделения)
            public TextBlock Status;
            public IssueLevel? Level;      // null = замечаний нет
            public List<Issue> Issues;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly CheckBox _autoApply;
        readonly CheckBox _iconFix;
        readonly CheckBox _preload;
        readonly HashSet<string> _preloadExclude;
        Row _selected;
        static readonly Brush SelectedBrush = Theme.SelectedFill;
        static readonly Brush SelectedBorder = Theme.SelectedBorder;
        readonly TextBlock _checkStatus;
        readonly TextBox _details;
        readonly StackPanel _list;
        readonly Button _checkButton;
        bool _sortByResult;
        Button _sortAlpha, _sortResult;
        readonly List<PluginGroup> _groups;
        bool _checked;
        readonly bool _ghLoaded;
        // Профили: выбранный в окне профиль и его сохранённый список отключённых плагинов
        readonly ComboBox _profileBox;
        string _profileName;
        HashSet<string> _savedDisabled;
        bool _switchingProfile;
        // Плагины, на которых Revit уже падал при загрузке или открытии Grasshopper (маркер падения), по ключу группы
        readonly Dictionary<string, CrashMarker.Suspect> _suspects = CrashMarker.SuspectsForCurrentRhino();

        public NextAction Next { get; private set; } = NextAction.None;

        /// Состояние окна, которое переносится при перерисовке после смены языка.
        public sealed class Carry
        {
            public string ProfileName;
            public HashSet<string> SavedDisabled;
            public HashSet<string> CurrentDisabled;
            public bool AutoApply, IconFix, Preload, SortByResult;
        }

        /// Не null — окно закрыто для смены языка, его нужно открыть заново с этим состоянием.
        public Carry Reopen { get; private set; }

        readonly ComboBox _languageBox;
        bool _languageLoading;

        public ManagerWindow(Carry carry = null)
        {
            Title = L.T($"RIR_PluginManager — плагины Grasshopper для Rhino.Inside ({Session.Describe()})",
                        $"RIR_PluginManager — Grasshopper plugins for Rhino.Inside ({Session.Describe()})");
            Width = 1000; Height = 720; MinWidth = 700; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            // Стеклянная тема: своё оформление окна без системной рамки
            Resources = Theme.Load();
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Foreground = Theme.Fg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var profile = PluginStore.LoadProfile();
            _profileName = carry?.ProfileName ?? profile.Name;
            _savedDisabled = new HashSet<string>(carry?.SavedDisabled ?? profile.Disabled, StringComparer.OrdinalIgnoreCase);
            if (carry != null) _sortByResult = carry.SortByResult;
            _ghLoaded = PluginStore.GrasshopperPluginsLoaded();

            var root = new DockPanel { Margin = new Thickness(10) };

            var header = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Text = L.T("Отмеченные плагины загрузятся в Grasshopper. Снятые с отметки временно отключаются " +
                           "(файлы переименовываются в *.off) и возвращаются сразу после загрузки Grasshopper " +
                           "или при закрытии Revit. Щёлкните по названию плагина: замечания появятся в панели справа.",
                           "Checked plugins will load in Grasshopper. Unchecked ones are temporarily disabled " +
                           "(the files are renamed to *.off) and restored right after Grasshopper loads " +
                           "or when Revit closes. Click a plugin name to see its notes in the panel on the right.")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var legend = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            legend.Inlines.Add(Glyph(IssueLevel.Error)); legend.Inlines.Add(L.T(" не загрузится   ", " will not load   "));
            legend.Inlines.Add(Glyph(IssueLevel.Warning)); legend.Inlines.Add(L.T(" возможны проблемы   ", " possible problems   "));
            legend.Inlines.Add(Glyph(IssueLevel.Info)); legend.Inlines.Add(L.T(" к сведению   ", " for information   "));
            legend.Inlines.Add(Glyph(null)); legend.Inlines.Add(L.T(" замечаний нет", " no notes"));
            legend.Margin = new Thickness(0, 6, 0, 0);

            _checkStatus = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.FgDim,
                Margin = new Thickness(0, 0, 0, 8),
                Text = L.T("Нажмите «Проверить плагины», чтобы найти известные причины проблем в этой версии Revit. " +
                           "Файлы читаются без загрузки, на работу Revit это не влияет.",
                           "Click \"Check plugins\" to find known causes of problems in this Revit version. " +
                           "Files are read without loading them; this does not affect Revit.")
            };
            DockPanel.SetDock(_checkStatus, Dock.Top);
            root.Children.Add(_checkStatus);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _checkButton = MakeButton(L.T("Проверить плагины", "Check plugins"), async (s, e) => await RunCheck());
            _checkButton.Style = (Style)Resources["PrimaryButton"];
            toolbar.Children.Add(_checkButton);
            toolbar.Children.Add(new TextBlock { Text = L.T("Сортировка:", "Sort:"), Foreground = Theme.FgDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 6) });
            _sortAlpha = MakeButton(L.T("А–Я", "A–Z"), (s, e) => SetSort(false));
            _sortResult = MakeButton(L.T("По результату", "By result"), (s, e) => SetSort(true));
            toolbar.Children.Add(_sortAlpha);
            toolbar.Children.Add(_sortResult);
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            if (_ghLoaded)
            {
                var warn = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Theme.Danger,
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = L.T("Профиль применён. Изменения применятся только после перезапуска Revit.", "The profile has been applied. Changes will take effect only after restarting Revit.")
                };
                DockPanel.SetDock(warn, Dock.Top);
                root.Children.Add(warn);
            }

            // ----- нижняя часть -----
            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);

            // Профили: свой набор для каждой версии Rhino (папка profiles\rhinoN)
            var profileRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            profileRow.Children.Add(new TextBlock
            {
                Text = L.T($"Профиль (Rhino {Session.RhinoMajorOrDefault}):", $"Profile (Rhino {Session.RhinoMajorOrDefault}):"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 6)
            });
            _profileBox = new ComboBox
            {
                MinWidth = 220,
                Margin = new Thickness(0, 0, 6, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.T("Выбор профиля загружает его отметки плагинов. Активным профиль становится после «Применить».", "Selecting a profile loads its plugin checkmarks. The profile becomes active after \"Apply\".")
            };
            _profileBox.SelectionChanged += (s, e) => OnProfileSelected();
            profileRow.Children.Add(_profileBox);
            profileRow.Children.Add(MakeButton(L.T("Новый", "New"), (s, e) => NewProfile()));
            profileRow.Children.Add(MakeButton(L.T("Переименовать", "Rename"), (s, e) => RenameProfile()));
            profileRow.Children.Add(MakeButton(L.T("Удалить", "Delete"), (s, e) => DeleteProfile()));
            bottom.Children.Add(profileRow);
            RefreshProfileList();

            _autoApply = new CheckBox
            {
                Content = L.T("Применять профиль автоматически при запуске Rhino", "Apply the profile automatically when Rhino starts"),
                IsChecked = carry?.AutoApply ?? profile.AutoApply,
                Margin = new Thickness(0, 0, 0, 8)
            };
            bottom.Children.Add(_autoApply);

            _iconFix = new CheckBox
            {
                Content = L.T($"Восстанавливать иконки старых плагинов без BinaryFormatter (сейчас: {IconFix.Status}; изменение действует после перезапуска Revit)",
                              $"Restore icons of old plugins without BinaryFormatter (now: {IconFix.Status}; a change takes effect after restarting Revit)"),
                IsChecked = carry?.IconFix ?? profile.IconFix,
                Margin = new Thickness(0, 0, 0, 8),
                IsEnabled = !Session.IsNetFramework              // на .NET Framework BinaryFormatter есть
            };
            bottom.Children.Add(_iconFix);

            _preloadExclude = profile.PreloadExclude;
            _preload = new CheckBox
            {
                Content = L.T("Заранее загружать новейшие версии общих библиотек плагинов (действует со следующего запуска Rhino)",
                              "Preload the newest versions of libraries shared by plugins (takes effect at the next Rhino start)"),
                IsChecked = carry?.Preload ?? profile.PreloadShared,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = L.T("Если несколько плагинов привозят одну библиотеку в разных версиях, до загрузки Grasshopper " +
                              "загружается самая новая — так все плагины смогут загрузиться. Исключения: строки preload_exclude= в файле " +
                              "settings-revit" + PluginStore.RevitVersion + ".txt.",
                              "If several plugins ship the same library in different versions, the newest one is loaded before Grasshopper " +
                              "loads, so that all plugins can load. Exceptions: preload_exclude= lines in " +
                              "settings-revit" + PluginStore.RevitVersion + ".txt.")
            };
            bottom.Children.Add(_preload);

            // Язык интерфейса: один выбор для всех версий Revit
            var languageRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            languageRow.Children.Add(new TextBlock
            {
                Text = "Язык / Language:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 6)
            });
            _languageBox = new ComboBox
            {
                MinWidth = 220,
                Margin = new Thickness(0, 0, 6, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.T("Один выбор для всех версий Revit. Надписи на ленте Revit сменятся после перезапуска Revit.",
                              "One choice for all Revit versions. Ribbon labels change after restarting Revit.")
            };
            _languageLoading = true;
            _languageBox.Items.Add(new ComboBoxItem { Content = L.T("Как в Revit", "Same as Revit"), Tag = L.Auto });
            _languageBox.Items.Add(new ComboBoxItem { Content = "Русский", Tag = L.Ru });
            _languageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = L.En });
            foreach (ComboBoxItem item in _languageBox.Items)
                if ((string)item.Tag == L.Setting) _languageBox.SelectedItem = item;
            _languageLoading = false;
            _languageBox.SelectionChanged += (s, e) => OnLanguageSelected();
            languageRow.Children.Add(_languageBox);
            bottom.Children.Add(languageRow);

            var selectRow = new WrapPanel();
            selectRow.Children.Add(MakeButton(L.T("Все", "All"), (s, e) => SetAll(true)));
            selectRow.Children.Add(MakeButton(L.T("Ни одного", "None"), (s, e) => SetAll(false)));
            selectRow.Children.Add(MakeButton(L.T("Снять ✖", "Uncheck ✖"), (s, e) => UncheckRisky(IssueLevel.Error)));
            selectRow.Children.Add(MakeButton(L.T("Снять ✖ и ⚠", "Uncheck ✖ and ⚠"), (s, e) => UncheckRisky(IssueLevel.Warning)));
            selectRow.Children.Add(MakeButton(L.T("Папка надстройки", "Add-in folder"), (s, e) => OpenDataDir()));
            selectRow.Children.Add(MakeButton(L.T("Вернуть все файлы", "Restore all files"), (s, e) => RestoreNow()));
            bottom.Children.Add(selectRow);

            var actionRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            actionRow.Children.Add(MakeButton(L.T("Применить", "Apply"), (s, e) => Finish(NextAction.None)));
            actionRow.Children.Add(MakeButton(L.T("Применить → Rhino", "Apply → Rhino"), (s, e) => Finish(NextAction.Rhino)));
            actionRow.Children.Add(MakeButton(L.T("Применить → Grasshopper", "Apply → Grasshopper"), (s, e) => Finish(NextAction.Grasshopper)));
            actionRow.Children.Add(MakeButton(L.T("Отмена", "Cancel"), (s, e) => DialogResult = false));
            bottom.Children.Add(actionRow);

            root.Children.Add(bottom);

            // ----- список плагинов слева, подробности справа -----
            _list = new StackPanel();
            _groups = PluginStore.Scan();
            if (_groups.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = L.T("Плагины не найдены. Дополнительные папки можно перечислить в файле folders.txt в папке надстройки.", "No plugins found. Extra folders can be listed in folders.txt in the add-in folder.")
                });
                _checkButton.IsEnabled = false;
            }
            foreach (var g in _groups)
            {
                var status = new TextBlock
                {
                    Text = "",
                    Width = 22,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"{g.Name}    [{g.Source}, " + L.T("файлов: ", "files: ") + g.Files.Count + DisabledNote(g) + "]" + SuspectNote(g)
                };
                // Галочка отдельно от текста: клик по квадрату включает/выключает плагин,
                // клик по строке выделяет её и показывает замечания справа.
                var cb = new CheckBox
                {
                    IsChecked = carry != null ? !carry.CurrentDisabled.Contains(g.Key) : !_savedDisabled.Contains(g.Key),
                    Margin = new Thickness(2, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(cb);
                content.Children.Add(status);
                content.Children.Add(label);

                var container = new Border
                {
                    Child = content,
                    Padding = new Thickness(2, 3, 2, 3),
                    Background = Brushes.Transparent,          // чтобы клик ловился по всей ширине строки
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var row = new Row { Group = g, Box = cb, Status = status, Container = container };
                container.PreviewMouseLeftButtonDown += (s, e) => Select(row);
                _rows.Add(row);
            }

            _details = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Padding = new Thickness(6),
                Text = L.T("Выберите плагин в списке слева (щелчок по названию).\n\nТекст можно прокручивать и копировать (Ctrl+A, Ctrl+C).",
                           "Select a plugin in the list on the left (click its name).\n\nThe text can be scrolled and copied (Ctrl+A, Ctrl+C).")
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });

            var listBorder = new Border
            {
                BorderBrush = Theme.GlassBorder,
                Background = Theme.PanelFill,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = _list,
                    Padding = new Thickness(4),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };
            // Легенда значков — прямо под списком
            var leftPanel = new DockPanel();
            DockPanel.SetDock(legend, Dock.Bottom);
            leftPanel.Children.Add(legend);
            var listCaption = SectionLabel(L.T("ПЛАГИНЫ", "PLUGINS"));
            DockPanel.SetDock(listCaption, Dock.Top);
            leftPanel.Children.Add(listCaption);
            leftPanel.Children.Add(listBorder);
            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            var splitter = new GridSplitter
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            _details.Background = Brushes.Transparent;
            _details.Foreground = Theme.Fg;
            _details.BorderThickness = new Thickness(0);
            _details.CaretBrush = Theme.Fg;
            var detailsBorder = new Border
            {
                Child = _details,
                CornerRadius = new CornerRadius(12),
                Background = Theme.PanelFill,
                BorderBrush = Theme.GlassBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };
            var rightPanel = new DockPanel();
            var detailsCaption = SectionLabel(L.T("ЗАМЕЧАНИЯ", "NOTES"));
            DockPanel.SetDock(detailsCaption, Dock.Top);
            rightPanel.Children.Add(detailsCaption);
            rightPanel.Children.Add(detailsBorder);
            Grid.SetColumn(rightPanel, 2);
            grid.Children.Add(rightPanel);

            root.Children.Add(grid);
            // Заголовок: перетаскивание окна и кнопка закрытия
            var titleBar = new DockPanel { Margin = new Thickness(18, 12, 12, 4), Background = Brushes.Transparent };
            var close = MakeButton("✕", (s, e) => DialogResult = false);
            close.Padding = new Thickness(10, 3, 10, 3);
            close.Margin = new Thickness(0);
            close.ToolTip = L.T("Закрыть (Esc)", "Close (Esc)");
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Plugin Manager",
                FontSize = 24,
                FontWeight = FontWeights.Bold
            });
            titleStack.Children.Add(new Border
            {
                Width = 28, Height = 3, Background = Theme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4)
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = $"RIR_PluginManager  ·  Rhino.Inside  ·  {Session.Describe()}" +
                       (Session.RhinoMajor > 0 ? "" : L.T("  (версия Rhino не определена, используется профиль Rhino 8)", "  (Rhino version not detected, the Rhino 8 profile is used)")),
                FontSize = 11,
                Foreground = Theme.FgDim
            });
            titleBar.Children.Add(titleStack);
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            root.Margin = new Thickness(18, 6, 18, 16);
            var outer = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            outer.Children.Add(titleBar);
            outer.Children.Add(root);

            Content = new Border
            {
                Margin = new Thickness(14),                      // место для тени окна
                CornerRadius = new CornerRadius(18),
                Background = Theme.WindowBackground,
                BorderBrush = Theme.GlassBorder,
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.22, Color = Colors.Black
                },
                Child = outer
            };
            PreviewKeyDown += OnListKeyDown;
            UpdateSortButtons();

            Resort();
        }

        // ---------- управление с клавиатуры ----------
        // ↑/↓ и W/S — перемещение выделения, Enter — включить/выключить выбранный плагин.
        // W и S определяются по физическому положению клавиши (скан-коду), поэтому работают в любой раскладке.

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);
        const uint ScanW = 0x11, ScanS = 0x1F;

        static uint ScanCodeOf(System.Windows.Input.Key key)
        {
            try { return MapVirtualKey((uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), 0); }
            catch { return 0; }
        }

        void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; e.Handled = true; return; }

            // В поле подробностей клавиши работают как обычно
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem) return;
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;

            var key = e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            uint sc = ScanCodeOf(key);

            if (key == System.Windows.Input.Key.Up || sc == ScanW) { MoveSelection(-1); e.Handled = true; }
            else if (key == System.Windows.Input.Key.Down || sc == ScanS) { MoveSelection(+1); e.Handled = true; }
            else if ((key == System.Windows.Input.Key.Enter || key == System.Windows.Input.Key.Return) && _selected != null)
            {
                _selected.Box.IsChecked = _selected.Box.IsChecked != true;
                e.Handled = true;
            }
        }

        void MoveSelection(int delta)
        {
            var visible = _list.Children.OfType<Border>().ToList();
            if (visible.Count == 0) return;
            int index = _selected == null ? -1 : visible.IndexOf(_selected.Container);
            int next = index < 0 ? 0 : Math.Max(0, Math.Min(visible.Count - 1, index + delta));
            var row = _rows.FirstOrDefault(r => r.Container == visible[next]);
            if (row == null) return;
            Select(row);
            row.Container.BringIntoView();
        }

        // ---------- проверка ----------

        async System.Threading.Tasks.Task RunCheck()
        {
            _checkButton.IsEnabled = false;
            _checkStatus.Foreground = Theme.FgDim;
            _checkStatus.Text = L.T("Проверка плагинов… (файлы читаются без загрузки, на работу Revit это не влияет)", "Checking plugins… (files are read without loading them; this does not affect Revit)");
            foreach (var r in _rows) { r.Status.Text = "…"; r.Status.Foreground = Theme.FgDim; }

            var loadedSnapshot = PluginChecker.SnapshotLoaded();
            var groups = _groups;
            try
            {
                bool preloadOn = _preload.IsChecked == true;
                var disabledNow = new HashSet<string>(_rows.Where(r => r.Box.IsChecked != true).Select(r => r.Group.Key),
                                                      StringComparer.OrdinalIgnoreCase);
                var exclude = _preloadExclude;
                var results = await Task.Run(() =>
                {
                    Dictionary<string, SharedLibPreloader.Item> preload = null;
                    if (preloadOn)
                        preload = SharedLibPreloader.Plan(groups, disabledNow, exclude, loadedSnapshot)
                                                    .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
                    return PluginChecker.Check(groups, loadedSnapshot, new CheckContext { Preload = preload });
                });
                ShowResults(results);
                if (_selected != null) ShowDetails(_selected);
                LogResults(results);
                _checked = true;
                Resort();
            }
            catch (Exception ex)
            {
                _checkStatus.Foreground = Theme.Danger;
                _checkStatus.Text = L.T("Проверку выполнить не удалось: ", "The check failed: ") + ex.Message;
                PluginStore.Log("Check error: " + ex);
            }
            finally
            {
                _checkButton.IsEnabled = true;
            }
        }

        void LogResults(Dictionary<string, List<Issue>> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("Результаты проверки плагинов:", "Plugin check results:"));
            foreach (var r in _rows.OrderBy(r => Rank(r.Level)).ThenBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.AppendLine($"  {GlyphChar(r.Level)} {r.Group.Name} [{r.Group.Source}]");
                if (results.TryGetValue(r.Group.Key, out var issues))
                    foreach (var i in issues.OrderByDescending(i => i.Level))
                        sb.AppendLine($"      {GlyphChar(i.Level)} {i.Text}");
            }
            PluginStore.Log(sb.ToString().TrimEnd());
        }

        // ---------- сортировка ----------

        static int Rank(IssueLevel? level) => level switch
        {
            IssueLevel.Error => 0,
            IssueLevel.Warning => 1,
            IssueLevel.Info => 2,
            _ => 3
        };

        void Resort()
        {
            if (_list == null || _rows.Count == 0) return;
            IEnumerable<Row> ordered = _rows.OrderBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase);
            if (_sortByResult)
            {
                ordered = _checked
                    ? _rows.OrderBy(r => Rank(r.Level)).ThenBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase)
                    : ordered;
                if (!_checked)
                    _checkStatus.Text = L.T("Сортировка по результату станет доступна после проверки: нажмите «Проверить плагины».", "Sorting by result becomes available after the check: click \"Check plugins\".");
            }
            _list.Children.Clear();
            foreach (var r in ordered) _list.Children.Add(r.Container);
        }

        void SetSort(bool byResult)
        {
            _sortByResult = byResult;
            UpdateSortButtons();
            Resort();
        }

        void UpdateSortButtons()
        {
            if (_sortAlpha == null || _sortResult == null) return;
            foreach (var b in new[] { _sortAlpha, _sortResult })
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(ForegroundProperty);
            }
            var active = _sortByResult ? _sortResult : _sortAlpha;
            active.Background = Theme.ActiveFill;
            active.Foreground = Theme.FgInverse;
        }

        // ---------- панель подробностей ----------

        void Select(Row row)
        {
            if (_selected != null)
            {
                _selected.Container.Background = Brushes.Transparent;
                _selected.Container.BorderBrush = Brushes.Transparent;
                _selected.Container.ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
            }
            _selected = row;
            row.Container.Background = SelectedBrush;
            row.Container.BorderBrush = SelectedBorder;
            row.Container.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Theme.FgInverse);
            ShowDetails(row);
        }

        void ShowDetails(Row row)
        {
            var g = row.Group;
            var sb = new StringBuilder();
            sb.AppendLine(g.Name);
            sb.AppendLine(L.T("Источник: ", "Source: ") + g.Source + DisabledNote(g));
            if (_suspects.TryGetValue(g.Key, out var crash))
                sb.AppendLine(L.T($"⚠ На этом плагине Revit упал при загрузке или открытии Grasshopper {crash.WhenUtc.ToLocalTime():dd.MM.yyyy HH:mm}. " +
                                  "Пометка снимется после запуска Grasshopper с этим плагином без сбоя.",
                                  $"⚠ Revit crashed on this plugin while Grasshopper was loading or opening ({crash.WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                                  "The mark is cleared after Grasshopper starts with this plugin without a crash."));
            if (g.Folder != null) sb.AppendLine(L.T("Папка: ", "Folder: ") + g.Folder);
            sb.AppendLine();

            if (!_checked)
                sb.AppendLine(L.T("Проверка не выполнялась. Нажмите «Проверить плагины».", "Not checked yet. Click \"Check plugins\"."));
            else if (row.Issues == null || row.Issues.Count == 0)
                sb.AppendLine(L.T("✔ Замечаний нет.", "✔ No notes."));
            else
            {
                sb.AppendLine(L.T("Замечания:", "Notes:"));
                foreach (var i in row.Issues.OrderByDescending(i => i.Level))
                {
                    sb.AppendLine($"{GlyphChar(i.Level)} {i.Text}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine(L.T("Файлы:", "Files:"));
            foreach (var f in g.Files) sb.AppendLine("  " + f);

            _details.Text = sb.ToString();
            _details.ScrollToHome();
        }

        // ---------- отображение результатов проверки ----------

        void ShowResults(Dictionary<string, List<Issue>> results)
        {
            int err = 0, warn = 0, info = 0, ok = 0;
            foreach (var row in _rows)
            {
                results.TryGetValue(row.Group.Key, out var issues);
                issues = issues ?? new List<Issue>();
                row.Level = issues.Count == 0 ? (IssueLevel?)null : issues.Max(i => i.Level);

                var glyph = Glyph(row.Level);
                row.Status.Text = glyph.Text;
                row.Status.Foreground = glyph.Foreground;

                row.Issues = issues;

                switch (row.Level)
                {
                    case IssueLevel.Error: err++; break;
                    case IssueLevel.Warning: warn++; break;
                    case IssueLevel.Info: info++; break;
                    default: ok++; break;
                }
            }
            _checkStatus.Foreground = Theme.Fg;
            _checkStatus.Text = L.T($"Проверка завершена: ✖ {err}   ⚠ {warn}   ℹ {info}   ✔ {ok}. " +
                                    "Это подсказка по известным причинам проблем, а не гарантия.",
                                    $"Check finished: ✖ {err}   ⚠ {warn}   ℹ {info}   ✔ {ok}. " +
                                    "This is a hint based on known causes of problems, not a guarantee.");
        }

        static string GlyphChar(IssueLevel? level) => level switch
        {
            IssueLevel.Error => "✖",
            IssueLevel.Warning => "⚠",
            IssueLevel.Info => "ℹ",
            _ => "✔"
        };

        static System.Windows.Documents.Run Glyph(IssueLevel? level) => new System.Windows.Documents.Run(GlyphChar(level))
        {
            Foreground = level switch
            {
                IssueLevel.Error => Theme.Error,
                IssueLevel.Warning => Theme.Warning,
                IssueLevel.Info => Theme.Info,
                _ => Theme.Ok
            },
            FontWeight = FontWeights.Bold
        };

        // ---------- кнопки ----------

        static TextBlock SectionLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.FgDim,
            Margin = new Thickness(4, 0, 0, 6)
        };

        static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                MinWidth = 80
            };
            b.Click += onClick;
            return b;
        }

        void SetAll(bool value)
        {
            foreach (var r in _rows) r.Box.IsChecked = value;
        }

        void UncheckRisky(IssueLevel threshold)
        {
            foreach (var r in _rows)
                if (r.Level.HasValue && r.Level.Value >= threshold)
                    r.Box.IsChecked = false;
            if (!_checked)
                MessageBox.Show(this, L.T("Сначала нажмите «Проверить плагины».", "Click \"Check plugins\" first."), Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OpenDataDir()
        {
            try
            {
                Directory.CreateDirectory(PluginStore.DataDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PluginStore.DataDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// "  ⚠ падение 09.10" — на плагине уже падал Revit (маркер падения).
        string SuspectNote(PluginGroup g) =>
            _suspects.TryGetValue(g.Key, out var s) ? L.T($"   ⚠ падение {s.WhenUtc.ToLocalTime():dd.MM}", $"   ⚠ crash {s.WhenUtc.ToLocalTime():MM-dd}") : "";

        /// ", сейчас отключён" / ", отключён в Revit 2027" (другим работающим Revit) / "".
        static string DisabledNote(PluginGroup g) =>
            g.DisabledFiles == 0 ? "" :
            g.DisabledElsewhere != null ? L.T($", отключён в {g.DisabledElsewhere}", $", disabled in {g.DisabledElsewhere}") : L.T(", сейчас отключён", ", disabled now");

        void RestoreNow()
        {
            // Возвращать имена безопасно в любой момент: уже загруженный Grasshopper их не перечитывает.
            var errors = PluginStore.RestoreAll();
            foreach (var e in errors) PluginStore.Log("RestoreNow: " + e);
            MessageBox.Show(this,
                errors.Count == 0
                    ? L.T("Файлам возвращены исходные имена. Профиль не изменён.\n\n" +
                          "Файлы, которые отключил другой работающий Revit, не тронуты: их вернёт тот Revit.",
                          "Original file names restored. The profile was not changed.\n\n" +
                          "Files disabled by another running Revit were not touched: that Revit will restore them.")
                    : L.T("Часть файлов вернуть не удалось:\n\n", "Some files could not be restored:\n\n") + string.Join(Environment.NewLine, errors.Take(15)),
                Title, MessageBoxButton.OK, errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            DialogResult = false;
        }

        // ---------- язык ----------

        void OnLanguageSelected()
        {
            if (_languageLoading) return;
            var setting = (_languageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? L.Auto;
            bool wasEnglish = L.English;
            try { L.Save(setting); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PluginStore.Log(L.T($"Язык интерфейса: {setting}", $"Interface language: {setting}"));
            if (L.English == wasEnglish) return;                      // язык фактически не изменился

            MessageBox.Show(this,
                L.T("Язык окна сменится сразу. Надписи на ленте Revit сменятся после перезапуска Revit.",
                    "The window language changes now. Ribbon labels in Revit change after restarting Revit."),
                "RIR_PluginManager", MessageBoxButton.OK, MessageBoxImage.Information);

            // Открыть окно заново на новом языке с текущими отметками и настройками
            Reopen = new Carry
            {
                ProfileName = _profileName,
                SavedDisabled = _savedDisabled,
                CurrentDisabled = CurrentDisabled(),
                AutoApply = _autoApply.IsChecked == true,
                IconFix = _iconFix.IsChecked == true,
                Preload = _preload.IsChecked == true,
                SortByResult = _sortByResult
            };
            DialogResult = false;
        }

        // ---------- профили ----------

        /// Отключённые плагины по текущим отметкам. Плагины профиля, которых сейчас нет на диске
        /// (например, временно удалённые), сохраняются.
        HashSet<string> CurrentDisabled()
        {
            var scanned = new HashSet<string>(_rows.Select(r => r.Group.Key), StringComparer.OrdinalIgnoreCase);
            var set = new HashSet<string>(_savedDisabled.Where(k => !scanned.Contains(k)), StringComparer.OrdinalIgnoreCase);
            foreach (var r in _rows)
                if (r.Box.IsChecked != true) set.Add(r.Group.Key);
            return set;
        }

        void RefreshProfileList()
        {
            _switchingProfile = true;
            try
            {
                _profileBox.Items.Clear();
                var names = PluginStore.ListProfiles();
                if (!names.Contains(_profileName, StringComparer.OrdinalIgnoreCase)) names.Insert(0, _profileName);
                foreach (var n in names) _profileBox.Items.Add(n);
                _profileBox.SelectedItem = names.First(n => string.Equals(n, _profileName, StringComparison.OrdinalIgnoreCase));
            }
            finally { _switchingProfile = false; }
        }

        void LoadProfileIntoList(string name)
        {
            _profileName = name;
            _savedDisabled = PluginStore.LoadDisabled(name);
            foreach (var r in _rows) r.Box.IsChecked = !_savedDisabled.Contains(r.Group.Key);
        }

        void OnProfileSelected()
        {
            if (_switchingProfile) return;
            var name = _profileBox.SelectedItem as string;
            if (name == null || name == _profileName) return;

            if (!CurrentDisabled().SetEquals(_savedDisabled))
            {
                var answer = MessageBox.Show(this,
                    L.T($"Отметки плагинов в профиле «{_profileName}» изменены. Сохранить их перед переключением?", $"Plugin checkmarks in profile \"{_profileName}\" have changed. Save them before switching?"),
                    Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                {
                    // Вернуть прежний выбор после завершения текущего события
                    Dispatcher.BeginInvoke(new Action(RefreshProfileList));
                    return;
                }
                if (answer == MessageBoxResult.Yes)
                {
                    try { PluginStore.SaveDisabled(_profileName, CurrentDisabled()); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, L.T("Не удалось сохранить профиль:\n", "Could not save the profile:\n") + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                        Dispatcher.BeginInvoke(new Action(RefreshProfileList));
                        return;
                    }
                }
            }
            LoadProfileIntoList(name);
        }

        void NewProfile()
        {
            var name = AskProfileName(L.T("Новый профиль", "New profile"),
                L.T("Имя нового профиля. В него попадут текущие отметки плагинов.", "Name of the new profile. It will get the current plugin checkmarks."), "", null);
            if (name == null) return;
            try
            {
                var disabled = CurrentDisabled();
                PluginStore.CreateProfile(name, disabled);
                _profileName = name;
                _savedDisabled = disabled;
                RefreshProfileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Не удалось создать профиль:\n", "Could not create the profile:\n") + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void RenameProfile()
        {
            var name = AskProfileName(L.T("Переименовать профиль", "Rename profile"),
                L.T($"Новое имя профиля «{_profileName}».", $"New name of profile \"{_profileName}\"."), _profileName, _profileName);
            if (name == null || name == _profileName) return;
            try
            {
                PluginStore.RenameProfile(_profileName, name);
                _profileName = name;
                RefreshProfileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Не удалось переименовать профиль:\n", "Could not rename the profile:\n") + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void DeleteProfile()
        {
            if (PluginStore.ListProfiles().Count <= 1)
            {
                MessageBox.Show(this, L.T("Это единственный профиль, его нельзя удалить.", "This is the only profile; it cannot be deleted."), Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, L.T($"Удалить профиль «{_profileName}»?", $"Delete profile \"{_profileName}\"?"), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                PluginStore.DeleteProfile(_profileName);
                // После удаления открывается активный профиль (или первый по алфавиту)
                LoadProfileIntoList(PluginStore.LoadProfile().Name);
                RefreshProfileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Не удалось удалить профиль:\n", "Could not delete the profile:\n") + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// Окно ввода имени профиля. Возвращает null при отмене.
        string AskProfileName(string caption, string prompt, string initial, string renaming)
        {
            var dlg = new Window
            {
                Title = caption,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                Resources = Theme.Load(),
                Background = Theme.PanelFill,
                Foreground = Theme.Fg,
                FontFamily = FontFamily,
                FontSize = FontSize
            };
            var panel = new StackPanel { Margin = new Thickness(16), Width = 380 };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var box = new TextBox { Text = initial, Padding = new Thickness(4), MaxLength = PluginStore.MaxProfileNameLength };
            panel.Children.Add(box);
            var error = new TextBlock { Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(error);

            string result = null;
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = MakeButton("OK", (s, e) =>
            {
                var name = box.Text.Trim();
                var err = PluginStore.ValidateProfileName(name, renaming);
                if (err != null) { error.Text = err; box.Focus(); return; }
                result = name;
                dlg.DialogResult = true;
            });
            ok.IsDefault = true;
            var cancel = MakeButton(L.T("Отмена", "Cancel"), (s, e) => dlg.DialogResult = false);
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };

            return dlg.ShowDialog() == true ? result : null;
        }

        void Finish(NextAction next)
        {
            // Включены плагины, на которых Revit уже падал при загрузке или открытии Grasshopper — предупредить
            if (!_ghLoaded)
            {
                var enabled = new HashSet<string>(_rows.Where(r => r.Box.IsChecked == true).Select(r => r.Group.Key),
                                                  StringComparer.OrdinalIgnoreCase);
                var risky = _suspects.Values.Where(s => enabled.Contains(s.Key)).ToList();
                if (risky.Count > 0)
                {
                    var answer = MessageBox.Show(this,
                        L.T("На этих плагинах Revit уже падал при загрузке или открытии Grasshopper:\n\n",
                            "Revit has already crashed on these plugins while Grasshopper was loading or opening:\n\n") + CrashMarker.List(risky) +
                        L.T("\n\nОтключить их и продолжить?\n\nДа — отключить и продолжить\nНет — продолжить как есть\nОтмена — вернуться к списку",
                            "\n\nDisable them and continue?\n\nYes — disable and continue\nNo — continue as is\nCancel — back to the list"),
                        Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Cancel) return;
                    if (answer == MessageBoxResult.Yes)
                        foreach (var r in _rows.Where(r => risky.Any(s => string.Equals(s.Key, r.Group.Key, StringComparison.OrdinalIgnoreCase))))
                            r.Box.IsChecked = false;
                }
            }

            // Настройки читаются с диска, чтобы сохранить активные профили других версий Rhino
            var profile = PluginStore.LoadProfile();
            profile.AutoApply = _autoApply.IsChecked == true;
            profile.IconFix = _iconFix.IsChecked == true;
            profile.PreloadShared = _preload.IsChecked == true;
            profile.PreloadExclude = _preloadExclude;
            profile.Name = _profileName;
            profile.Disabled = CurrentDisabled();

            try
            {
                PluginStore.SaveProfile(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Не удалось сохранить профиль:\n", "Could not save the profile:\n") + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_ghLoaded)
            {
                MessageBox.Show(this,
                    L.T($"Профиль «{_profileName}» сохранён. Изменения применятся только после перезапуска Revit.",
                        $"Profile \"{_profileName}\" saved. Changes will take effect only after restarting Revit."),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var errors = PluginStore.Apply(profile, out _);
                if (errors.Count > 0)
                {
                    var text = string.Join(Environment.NewLine, errors.Take(15));
                    if (errors.Count > 15) text += Environment.NewLine + L.T($"... и ещё {errors.Count - 15} (см. лог в папке logs)", $"... and {errors.Count - 15} more (see the log in the logs folder)");
                    foreach (var e in errors) PluginStore.Log("Apply: " + e);
                    MessageBox.Show(this, L.T("Часть файлов обработать не удалось:\n\n", "Some files could not be processed:\n\n") + text,
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            Next = next;
            DialogResult = true;
        }
    }
}
