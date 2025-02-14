﻿using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using eft_dma_radar.Source.Misc;
using eft_dma_radar.Source.Tarkov;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using static eft_dma_radar.LootFilter;

namespace eft_dma_radar
{
    public partial class frmMain : Form
    {
        private readonly Config _config;
        private readonly SKGLControl _mapCanvas;
        private readonly Stopwatch _fpsWatch = new();
        private readonly object _renderLock = new();
        private readonly System.Timers.Timer _mapChangeTimer = new(900);
        private readonly List<Map> _maps = new(); // Contains all maps from \\Maps folder

        private float _uiScale = 1.0f;
        private float _aimviewWindowSize = 200;
        private Player _closestPlayerToMouse = null;
        private DevLootItem _closestItemToMouse = null;
        private int? _mouseOverGroup = null;
        private int _fps = 0;
        private int _mapSelectionIndex = 0;
        private Map _selectedMap;
        private SKBitmap[] _loadedBitmaps;
        private MapPosition _mapPanPosition = new();

        #region Getters
        /// <summary>
        /// Radar has found Escape From Tarkov process and is ready.
        /// </summary>
        private bool Ready
        {
            get => Memory.Ready;
        }

        /// <summary>
        /// Radar has found Local Game World.
        /// </summary>
        private bool InGame
        {
            get => Memory.InGame;
        }

        private bool IsAtHideout
        {
            get => Memory.InHideout;
        }
        private string CurrentMapName
        {
            get => Memory.MapName;
        }

        /// <summary>
        /// LocalPlayer (who is running Radar) 'Player' object.
        /// </summary>
        private Player LocalPlayer
        {
            get =>
                Memory.Players?.FirstOrDefault(x => x.Value.Type is PlayerType.LocalPlayer).Value;
        }

        /// <summary>
        /// All Players in Local Game World (including dead/exfil'd) 'Player' collection.
        /// </summary>
        private ReadOnlyDictionary<string, Player> AllPlayers
        {
            get => Memory.Players;
        }

        /// <summary>
        /// Contains all loot in Local Game World.
        /// </summary>
        private LootManager Loot
        {
            get => Memory.Loot;
        }
        /// <summary>
        /// Contains all 'Hot' grenades in Local Game World, and their position(s).
        /// </summary>
        private ReadOnlyCollection<Grenade> Grenades
        {
            get => Memory.Grenades;
        }

        /// <summary>
        /// Radar is in the process of loading loot. Radar may be paused during this operation.
        /// </summary>
        private bool LoadingLoot
        {
            get => Memory.LoadingLoot;
        }

        /// <summary>
        /// Contains all 'Exfils' in Local Game World, and their status/position(s).
        /// </summary>
        private ReadOnlyCollection<Exfil> Exfils
        {
            get => Memory.Exfils;
        }
        #endregion

        #region Constructor
        /// <summary>
        /// GUI Constructor.
        /// </summary>
        public frmMain()
        {
            _config = Program.Config; // get ref to config
            InitializeComponent();
            // init skia
            _mapCanvas = new SKGLControl()
            {
                Size = new Size(50, 50),
                Dock = DockStyle.Fill,
                VSync = _config.Vsync // cap fps to refresh rate, reduce tearing
            };

            tabRadar.Controls.Add(_mapCanvas); // place Radar Map Canvas on top of TabPage1
            chkMapFree.Parent = _mapCanvas; // change parent for checkBox_MapFree 'button'
            trkUIScale.ValueChanged += trkUIScale_ValueChanged; // Handle UI Adjustments

            LoadConfig();
            LoadMaps();
            _mapChangeTimer.AutoReset = false;
            _mapChangeTimer.Elapsed += MapChangeTimer_Elapsed;

            this.DoubleBuffered = true; // Prevent flickering
            this.Shown += frmMain_Shown;
            _mapCanvas.PaintSurface += MapCanvas_PaintSurface; // Radar Drawing Event
            _mapCanvas.MouseMove += MapCanvas_MouseMovePlayer; // Handle mouseover events on radar
            tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
            _mapCanvas.MouseClick += MapCanvas_MouseClick;
            lstViewPMCHistory.MouseDoubleClick += lstViewPMCHistory_MouseDoubleClick;
            _fpsWatch.Start(); // fps counter
        }
        #endregion

        #region Events
        /// <summary>
        /// Event fires when MainForm becomes visible. Loops endlessly but is asynchronously non-blocking.
        /// </summary>
        private async void frmMain_Shown(object sender, EventArgs e)
        {
            while (_mapCanvas.GRContext is null)
                await Task.Delay(1);
            _mapCanvas.GRContext.SetResourceCacheLimit(503316480); // Fixes low FPS on big maps
            while (true)
            {
                await Task.Run(() => Thread.SpinWait(50000)); // High performance async delay
                _mapCanvas.Refresh(); // draw next frame
            }
        }

        /// <summary>
        /// Event fires when switching Tab Pages.
        /// </summary>
        private void TabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl.SelectedIndex == 2) // Player Loadouts Tab
            {
                rchTxtPlayerInfo.Clear();
                var enemyPlayers = this.AllPlayers
                    ?.Select(x => x.Value)
                    .Where(x => x.IsHumanHostileActive)
                    .ToList()
                    .OrderBy(x => x.GroupID)
                    .ThenBy(x => x.Name);
                if (this.InGame && enemyPlayers is not null)
                {
                    var sb = new StringBuilder();
                    sb.Append(@"{\rtf1\ansi");

                    foreach (var player in enemyPlayers)
                    {
                        string title = $"*** {player.Name} ({player.Type})  L:{player.Lvl}";

                        if (player.GroupID != -1)
                            title += $" G:{player.GroupID}";
                        if (player.KDA != -1f)
                            title += $" KD{player.KDA.ToString("n1")}";

                        sb.Append(@$"\b {title} \b0 ");
                        sb.Append(@" \line ");

                        var gear = player.Gear; // cache ref

                        if (gear is not null)
                            foreach (var slot in gear)
                            {
                                sb.Append(@$"\b {slot.Key}: \b0 ");
                                sb.Append(slot.Value.Long); // Use long item name
                                sb.Append(@" \line ");
                            }
                        else
                            sb.Append(@" ERROR retrieving gear \line");
                        sb.Append(@" \line ");
                    }
                    sb.Append(@"}");
                    rchTxtPlayerInfo.Rtf = sb.ToString();
                }
            }
            else if (tabControl.SelectedIndex == 3) // Player History Tab
            {
                lstViewPMCHistory.Items.Clear(); // Clear old view
                lstViewPMCHistory.Items.AddRange(Player.History); // Obtain new view
                lstViewPMCHistory.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent); // resize Player History columns automatically
            }
        }

        /// <summary>
        /// Fired when 'Is Active' checkbox for a filter is changed, saves config & then applies the lootfilter
        /// </summary>
        private void chkLootFilterActive_CheckedChanged(object sender, EventArgs e)
        {
            LootFilter selectedFilter = (LootFilter)cboFilters.SelectedItem;
            selectedFilter.IsActive = chkLootFilterActive.Checked;

            Config.SaveConfig(_config);
            this.Loot?.ApplyFilter();
        }

        /// <summary>
        /// Fired when NightVision checkbox has been adjusted
        /// </summary>
        private void chkNightVision_CheckedChanged(object sender, EventArgs e)
        {
            _config.NightVisionEnabled = chkNightVision.Checked;
        }

        /// <summary>
        /// Fired when ThermalVision checkbox has been adjusted
        /// </summary>
        private void chkThermalVision_CheckedChanged(object sender, EventArgs e)
        {
            _config.ThermalVisionEnabled = chkThermalVision.Checked;
        }

        /// <summary>
        /// Fired when OpticThermalVision checkbox has been adjusted
        /// </summary>
        private void chkOpticThermalVision_CheckedChanged(object sender, EventArgs e)
        {
            _config.OpticThermalVisionEnabled = chkOpticThermalVision.Checked;
        }

        /// <summary>
        /// Fired when NoVisor checkbox has been adjusted
        /// </summary>
        private void chkNoVisor_CheckedChanged(object sender, EventArgs e)
        {
            _config.NoVisorEnabled = chkNoVisor.Checked;
        }

        /// <summary>
        /// Fired when NoRecoil checkbox has been adjusted
        /// </summary>
        private void chkNoRecoil_CheckedChanged(object sender, EventArgs e)
        {
            _config.NoRecoilEnabled = chkNoRecoil.Checked;
        }

        /// <summary>
        /// Fired when Max Stamina checkbox has been adjusted
        /// </summary>
        private void chkMaxStamina_CheckedChanged(object sender, EventArgs e)
        {
            _config.MaxStaminaEnabled = chkMaxStamina.Checked;
        }

        /// <summary>
        /// Fired when NoSway checkbox has been adjusted
        /// </summary>
        private void chkNoSway_CheckedChanged(object sender, EventArgs e)
        {
            _config.NoSwayEnabled = chkNoSway.Checked;
        }

        /// <summary>
        /// Fired when Chams checkbox has been adjusted
        /// </summary>
        private void chkChams_CheckedChanged(object sender, EventArgs e)
        {
            var allPlayers = this.AllPlayers
                ?.Select(x => x.Value)
                .Where(x => !x.HasExfild);

            ulong playerBase = 0;
            if (allPlayers is not null)
            {
                foreach (var player in allPlayers)
                {
                    if (player.Type == PlayerType.LocalPlayer)
                    {
                        continue;
                    }
                    if (player.Type == PlayerType.AIOfflineScav)
                    {
                        playerBase = player.Base;
                    }
                    if (playerBase == 0)
                    {
                        continue;
                    }
                    if (chkChams.Checked)
                    {
                        Chams.ClothingChams(playerBase);
                    }
                    else
                    {
                        Chams.RestorePointers();
                    }
                }
            }

        }

        /// <summary>
        /// Fired when item is removed from a filter & saves config
        /// </summary>
        private void btnLootFilterRemoveItem_Click(object sender, EventArgs e)
        {
            if (lstViewLootFilter.SelectedItems.Count > 0)
            {
                LootFilter selectedFilter = (LootFilter)cboFilters.SelectedItem;
                ListViewItem selectedItem = lstViewLootFilter.SelectedItems[0];

                if (selectedItem != null)
                {
                    DevLootItem lootItem = (DevLootItem)selectedItem.Tag;

                    selectedItem.Remove();
                    selectedFilter.Items.Remove(lootItem.Item.id);

                    Config.SaveConfig(_config);
                    this.Loot?.ApplyFilter();
                }
            }
        }

        /// <summary>
        /// Fired when item is added to a filter & saves config
        /// </summary>
        private void btnLootFilterAddItem_Click(object sender, EventArgs e)
        {
            if (cboLootItems.SelectedIndex != -1)
            {
                LootFilter selectedFilter = (LootFilter)cboFilters.SelectedItem;
                DevLootItem selectedItem = (DevLootItem)cboLootItems.SelectedItem;

                if (selectedFilter != null)
                {
                    if (selectedFilter.Items.Contains(selectedItem.Item.id))
                    {
                        return; // item already exists
                    }
                    else
                    {
                        ListViewItem listItem = new ListViewItem();
                        listItem.Text = selectedItem.Item.id;
                        listItem.SubItems.Add(selectedItem.Item.name);
                        listItem.SubItems.Add(selectedItem.Item.shortName);
                        listItem.SubItems.Add(TarkovDevAPIManager.FormatNumber(TarkovDevAPIManager.GetItemValue(selectedItem.Item)));
                        listItem.Tag = selectedItem;

                        lstViewLootFilter.Items.Add(listItem);
                        selectedFilter.Items.Add(selectedItem.Item.id);

                        Config.SaveConfig(_config);
                        this.Loot?.ApplyFilter();
                    }
                }
            }
        }

        /// <summary>
        /// Fired when an item to search for is being typed
        /// </summary>
        private void txtItemFilter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter)
            {
                var itemToSearch = txtItemFilter.Text;
                List<DevLootItem> lootList = TarkovDevAPIManager.AllItems
                    .Select(x => x.Value)
                    .Where(x => x.Label.Contains(itemToSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Label)
                    .Take(25)
                    .ToList();

                cboLootItems.DataSource = lootList;
                cboLootItems.DisplayMember = "Label";
            }
        }

        /// <summary>
        /// Fired when the current filter is changed
        /// </summary>
        private void cboFilters_SelectedValueChanged(object sender, EventArgs e)
        {
            UpdateLootFilterList();
        }

        /// <summary>
        /// Fired when UI Scale Trackbar is Adjusted
        /// </summary>
        private void trkUIScale_ValueChanged(object sender, EventArgs e)
        {
            _uiScale = (.01f * trkUIScale.Value);
            lblUIScale.Text = $"UI Scale {_uiScale.ToString("n2")}";
            #region UpdatePaints
            SKPaints.PaintMouseoverGroup.StrokeWidth = 3 * _uiScale;
            SKPaints.TextMouseoverGroup.TextSize = 12 * _uiScale;
            SKPaints.PaintBase.StrokeWidth = 3 * _uiScale;
            SKPaints.TextBase.TextSize = 12 * _uiScale;
            SKPaints.PaintDeathMarker.StrokeWidth = 3 * _uiScale;
            SKPaints.PaintLoot.StrokeWidth = 3 * _uiScale;
            SKPaints.TextLoot.TextSize = 13 * _uiScale;
            SKPaints.PaintTransparentBacker.StrokeWidth = 1 * _uiScale;
            SKPaints.PaintAimviewCrosshair.StrokeWidth = 1 * _uiScale;
            SKPaints.TextRadarStatus.TextSize = 48 * _uiScale;
            SKPaints.PaintGrenades.StrokeWidth = 3 * _uiScale;
            SKPaints.PaintExfilOpen.StrokeWidth = 1 * _uiScale;
            SKPaints.PaintExfilPending.StrokeWidth = 1 * _uiScale;
            SKPaints.PaintExfilClosed.StrokeWidth = 1 * _uiScale;
            #endregion
            _aimviewWindowSize = 200 * _uiScale;
        }

        /// <summary>
        /// Event fires when the "Map Free" or "Map Follow" checkbox (button) is clicked on the Main Window.
        /// </summary>
        private void chkMapFree_CheckedChanged(object sender, EventArgs e)
        {
            if (chkMapFree.Checked)
            {
                chkMapFree.Text = "Map Follow";
                lock (_renderLock)
                {
                    var localPlayer = this.LocalPlayer;
                    if (localPlayer is not null)
                    {
                        var localPlayerMapPos = localPlayer.Position.ToMapPos(_selectedMap);
                        _mapPanPosition = new MapPosition()
                        {
                            X = localPlayerMapPos.X,
                            Y = localPlayerMapPos.Y,
                            Height = localPlayerMapPos.Height
                        };
                    }
                }
            }
            else
                chkMapFree.Text = "Map Free";
        }

        /// <summary>
        /// Handles mouse movement on Map Canvas, specifically checks if mouse moves close to a 'Player' position.
        /// </summary>
        private void MapCanvas_MouseMovePlayer(object sender, MouseEventArgs e)
        {
            if (this.InGame) // Must be in-game
            {
                var players = this.AllPlayers
                    ?.Select(x => x.Value)
                    .Where(x => x.Type is not PlayerType.LocalPlayer && !x.HasExfild); // Get all players except LocalPlayer & Exfil'd Players

                var loot = this.Loot?.Filter?.Select(x => x);

                if ((players is not null && players.Any()) || (loot is not null && loot.Any()))
                {
                    var mouse = new Vector2(e.X, e.Y); // Get current mouse position in control

                    if (players is not null && players.Any())
                    {
                        var closestPlayer = players.Aggregate(
                            (x1, x2) =>
                                Vector2.Distance(x1.ZoomedPosition, mouse)
                                < Vector2.Distance(x2.ZoomedPosition, mouse)
                                    ? x1
                                    : x2
                        ); // Get player object 'closest' to mouse position

                        if (closestPlayer is not null)
                        {
                            var dist = Vector2.Distance(closestPlayer.ZoomedPosition, mouse);
                            if (dist < 12) // See if 'closest object' is close enough.
                            {
                                _closestPlayerToMouse = closestPlayer; // Save ref to closest player object
                                if (closestPlayer.IsHumanHostile && closestPlayer.GroupID != -1)
                                    _mouseOverGroup = closestPlayer.GroupID; // Set group ID for closest player(s)
                                else
                                    _mouseOverGroup = null; // Clear Group ID
                            }
                            else
                                ClearPlayerRefs();
                        }
                        else
                            ClearPlayerRefs();
                    }
                    else
                        ClearPlayerRefs();

                    if (loot is not null && loot.Any())
                    {
                        var closestItem = loot.Aggregate(
                            (x1, x2) =>
                                Vector2.Distance(x1.ZoomedPosition, mouse)
                                < Vector2.Distance(x2.ZoomedPosition, mouse)
                                    ? x1
                                    : x2
                        ); // Get loot object 'closest' to mouse position

                        if (closestItem is not null)
                        {
                            var dist = Vector2.Distance(closestItem.ZoomedPosition, mouse);
                            if (dist < 12) // See if 'closest object' is close enough.
                            {
                                _closestItemToMouse = closestItem; // Save ref to closest item object
                            }
                            else
                                ClearItemRefs();
                        }
                        else
                            ClearItemRefs();
                    }
                    else
                        ClearItemRefs();
                }
                else
                {
                    ClearPlayerRefs();
                    ClearItemRefs();
                }
            }
            else
            {
                ClearPlayerRefs();
                ClearItemRefs();
            }

            void ClearPlayerRefs()
            {
                _closestPlayerToMouse = null;
                _mouseOverGroup = null;
            }

            void ClearItemRefs()
            {
                _closestItemToMouse = null;
            }
        }

        /// <summary>
        /// Event fires when Map Setup box is checked/unchecked.
        /// </summary>
        private void chkShowMapSetup_CheckedChanged(object sender, EventArgs e)
        {
            if (chkShowMapSetup.Checked)
            {
                grpMapSetup.Visible = true;
                txtMapSetupX.Text = _selectedMap.ConfigFile.X.ToString();
                txtMapSetupY.Text = _selectedMap.ConfigFile.Y.ToString();
                txtMapSetupScale.Text = _selectedMap.ConfigFile.Scale.ToString();
            }
            else
                grpMapSetup.Visible = false;
        }

        /// <summary>
        /// Event fires when Restart Game button is clicked in Settings.
        /// </summary>
        private void btnRestartRadar_Click(object sender, EventArgs e)
        {
            Memory.Restart();
        }

        /// <summary>
        /// Event fires when Apply button is clicked in the "Map Setup Groupbox".
        /// </summary>
        private void btnMapSetupApply_Click(object sender, EventArgs e)
        {
            if (float.TryParse(txtMapSetupX.Text, out float x)
                && float.TryParse(txtMapSetupY.Text, out float y)
                && float.TryParse(txtMapSetupScale.Text, out float scale))
            {
                lock (_renderLock)
                {
                    _selectedMap.ConfigFile.X = x;
                    _selectedMap.ConfigFile.Y = y;
                    _selectedMap.ConfigFile.Scale = scale;
                    _selectedMap.ConfigFile.Save(_selectedMap);
                }
            }
            else
            {
                throw new Exception("INVALID float values in Map Setup.");
            }
        }

        /// <summary>
        /// Allows panning the map when in "Free" mode.
        /// </summary>
        private void MapCanvas_MouseClick(object sender, MouseEventArgs e)
        {
            if (chkMapFree.Checked)
            {
                var center = new SKPoint(_mapCanvas.Width / 2, _mapCanvas.Height / 2); // Get center of canvas

                lock (_renderLock)
                {
                    _mapPanPosition = new MapPosition() // Pan based on distance/direction from center
                    {
                        X = _mapPanPosition.X + (e.X - center.X),
                        Y = _mapPanPosition.Y + (e.Y - center.Y)
                    };
                }
            }
        }

        /// <summary>
        /// Executes map change after a short delay, in case switching through maps quickly to reduce UI lag.
        /// </summary>
        private void MapChangeTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            this.BeginInvoke(
                new MethodInvoker(
                    delegate
                    {
                        btnToggleMap.Enabled = false;
                        btnToggleMap.Text = "Loading...";
                    }
                )
            );

            lock (_renderLock)
            {
                try
                {
                    _selectedMap = _maps[_mapSelectionIndex]; // Swap map

                    if (_loadedBitmaps is not null)
                    {
                        foreach (var bitmap in _loadedBitmaps)
                            bitmap?.Dispose(); // Cleanup resources
                    }

                    _loadedBitmaps = new SKBitmap[_selectedMap.ConfigFile.MapLayers.Count];

                    for (int i = 0; i < _loadedBitmaps.Length; i++)
                    {
                        using (
                            var stream = File.Open(
                                _selectedMap.ConfigFile.MapLayers[i].Filename,
                                FileMode.Open,
                                FileAccess.Read))
                        {
                            _loadedBitmaps[i] = SKBitmap.Decode(stream); // Load new bitmap(s)
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"ERROR loading {_selectedMap.ConfigFile.MapLayers[0].Filename}: {ex}"
                    );
                }
                finally
                {
                    this.BeginInvoke(
                        new MethodInvoker(
                            delegate
                            {
                                btnToggleMap.Enabled = true;
                                btnToggleMap.Text = "Toggle Map (F5)";
                            }
                        )
                    );
                }
            }
        }

        /// <summary>
        /// Event fires when the Map button is clicked in Settings.
        /// </summary>
        private void btnToggleMap_Click(object sender, EventArgs e)
        {
            ToggleMap();
        }

        /// <summary>
        /// Copies Player "BSG ID" to Clipboard upon double clicking History Entry.
        /// </summary>
        private void lstViewPMCHistory_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            var info = lstViewPMCHistory.HitTest(e.X, e.Y);
            var view = info.Item;

            if (view is not null)
            {
                var entry = (PlayerHistoryEntry)view.Tag;

                if (entry is not null)
                {
                    var acctId = entry.ToString();

                    if (acctId is not null && acctId != string.Empty)
                    {
                        Clipboard.SetText(acctId); // Copy BSG ID to clipboard
                        MessageBox.Show($"Copied '{acctId}' to Clipboard!");
                    }
                }
            }
        }

        /// <summary>
        /// Adjusts min regular loot based on slider value
        /// </summary>
        private void trkRegularLootValue_Scroll(object sender, EventArgs e)
        {
            int value = trkRegularLootValue.Value * 1000;
            lblRegularLootDisplay.Text = TarkovDevAPIManager.FormatNumber(value);
            _config.MinLootValue = value;

            if (Loot is not null)
            {
                Loot.ApplyFilter();
            }
        }

        /// <summary>
        /// Adjusts min important loot based on slider value
        /// </summary>
        private void trkImportantLootValue_Scroll(object sender, EventArgs e)
        {
            int value = trkImportantLootValue.Value * 1000;
            lblImportantLootDisplay.Text = TarkovDevAPIManager.FormatNumber(value);
            _config.MinImportantLootValue = value;

            if (Loot is not null)
            {
                Loot.ApplyFilter();
            }
        }

        /// <summary>
        /// Refreshes the loot in the match
        /// </summary>
        private void btnRefreshLoot_Click(object sender, EventArgs e)
        {
            Memory.RefreshLoot();
        }

        /// <summary>
        /// Handles color modification for loot filters
        /// </summary>
        private void picLootFilterPreview_Click(object sender, EventArgs e)
        {
            if (colDialog.ShowDialog() == DialogResult.OK)
            {
                picLootFilterEditColor.BackColor = colDialog.Color;
            }
        }

        /// <summary>
        /// Handles the edit/save button for modifying filters
        /// </summary>
        private void btnEditSaveFilter_Click(object sender, EventArgs e)
        {
            string btnText = btnEditSaveFilter.Text;
            if (btnText == "Edit")
            { /// show edit button
                txtLootFilterEditName.Enabled = true;
                picLootFilterEditColor.Enabled = true;
                chkLootFilterEditActive.Enabled = true;
                btnEditSaveFilter.Text = "Save";
                btnCancelEditFilter.Visible = true;
            }
            else if (btnText == "Save")
            { // save functionality
                if (HasUnsavedFilterEditChanges() && MessageBox.Show("Are you sure you want to save changes?", "Are you sure?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    if (txtLootFilterEditName.Text.Length > 0)
                    {
                        btnCancelEditFilter.Visible = false;
                        btnEditSaveFilter.Text = "Edit";

                        txtLootFilterEditName.Enabled = false;
                        picLootFilterEditColor.Enabled = false;
                        chkLootFilterEditActive.Enabled = false;

                        LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;
                        int index = _config.Filters.IndexOf(selectedFilter);

                        Color cols = picLootFilterEditColor.BackColor;
                        selectedFilter.Name = txtLootFilterEditName.Text;
                        selectedFilter.IsActive = chkLootFilterEditActive.Checked;
                        selectedFilter.Color = new Colors
                        {
                            R = cols.R,
                            G = cols.G,
                            B = cols.B,
                            A = cols.A
                        };

                        _config.Filters.RemoveAt(index);
                        _config.Filters.Insert(index, selectedFilter);

                        Config.SaveConfig(_config);
                        UpdateEditFilterListBox(lstEditLootFilters.SelectedIndex);
                    }
                    else
                    {
                        MessageBox.Show("Add some text to the textbox (minimum 1 character)", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Handles cancel button when modifying filters
        /// </summary>
        private void btnCancelEditFilter_Click(object sender, EventArgs e)
        {
            if (HasUnsavedFilterEditChanges())
            {
                if (MessageBox.Show("Are you sure you want to cancel changes?", "Are you sure?", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    btnCancelEditFilter.Visible = false;
                    btnEditSaveFilter.Text = "Edit";

                    txtLootFilterEditName.Enabled = false;
                    picLootFilterEditColor.Enabled = false;
                    chkLootFilterEditActive.Enabled = false;

                    UpdateEditFilterListBox(lstEditLootFilters.SelectedIndex);
                }
            }
            else
            {
                btnCancelEditFilter.Visible = false;
                btnEditSaveFilter.Text = "Edit";

                txtLootFilterEditName.Enabled = false;
                picLootFilterEditColor.Enabled = false;
                chkLootFilterEditActive.Enabled = false;
            }
        }

        /// <summary>
        /// Updates the ListBox with filters & applys the loot filter if in-game
        /// </summary>
        /// <param name="index">Optional argument to set the selected index manually for the ListBox containing filters</param>
        private void UpdateEditFilterListBox(int index = 0)
        {
            lstEditLootFilters.Items.Clear();

            List<LootFilter> lootFilters = _config.Filters.OrderBy(lf => lf.Order).ToList();

            foreach (LootFilter filter in lootFilters)
            {
                lstEditLootFilters.Items.Add(filter);
            }

            if (lootFilters.Count > 0)
            {
                lstEditLootFilters.SetSelected(index, true);
                UpdateLootFilterComboBoxes();
            }

            if (Loot is not null)
            {
                Loot.ApplyFilter();
            }
        }

        /// <summary>
        /// Updates the information displayed about a filter
        /// </summary>
        private void lstEditLootFilters_SelectedIndexChanged(object sender, EventArgs e)
        {
            LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;

            if (selectedFilter != null)
            {
                Colors col = selectedFilter.Color;

                txtLootFilterEditName.Text = selectedFilter.Name;
                picLootFilterEditColor.BackColor = Color.FromArgb(col.A, col.R, col.G, col.B);
                chkLootFilterEditActive.Checked = selectedFilter.IsActive;
            }
        }

        /// <summary>
        /// Shifts the order (aka priority) of the filter up
        /// </summary>
        private void btnFilterPriorityUp_Click(object sender, EventArgs e)
        {
            LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;

            if (selectedFilter != null)
            {
                if (selectedFilter.Order != 1)
                { // make sure we dont out of bounds ourself
                    int tmpOrder = selectedFilter.Order;
                    int index = selectedFilter.Order - 1;

                    LootFilter swapFilter = _config.Filters.FirstOrDefault(f => f.Order == selectedFilter.Order - 1);

                    selectedFilter.Order = swapFilter.Order;
                    swapFilter.Order = tmpOrder;

                    Config.SaveConfig(_config);
                    UpdateEditFilterListBox(index - 1);
                }
            }
        }

        /// <summary>
        /// Shifts the order (aka priority) of the filter down
        /// </summary>
        private void btnFilterPriorityDown_Click(object sender, EventArgs e)
        {
            LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;

            if (selectedFilter != null)
            {
                if (selectedFilter.Order != _config.Filters.Count)
                { // make sure we dont out of bounds ourself
                    int tmpOrder = selectedFilter.Order;
                    int index = selectedFilter.Order - 1;

                    LootFilter swapFilter = _config.Filters.FirstOrDefault(f => f.Order == index + 2);

                    selectedFilter.Order = swapFilter.Order;
                    swapFilter.Order = tmpOrder;

                    Config.SaveConfig(_config);
                    UpdateEditFilterListBox(index + 1);
                }
            }
        }

        /// <summary>
        /// Creates a new filter
        /// </summary>
        private void btnAddNewFilter_Click(object sender, EventArgs e)
        {
            LootFilter newFilter = new LootFilter()
            {
                Order = _config.Filters.Count + 1,
                IsActive = true,
                Name = "New Filter",
                Items = new List<String>(),
                Color = new Colors()
                {
                    R = 255,
                    G = 255,
                    B = 255,
                    A = 255
                }
            };

            _config.Filters.Add(newFilter);
            Config.SaveConfig(_config);
            UpdateEditFilterListBox();
        }

        /// <summary>
        /// Nukes a filter & removes it then updates the filter lists
        /// </summary>
        private void btnRemoveFilter_Click(object sender, EventArgs e)
        {
            LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;

            if (selectedFilter != null)
            {
                if (_config.Filters.Count == 1)
                {
                    if (MessageBox.Show("Removing the last filter will automatically create a blank one", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error) == DialogResult.OK)
                    {
                        _config.Filters.RemoveAt(selectedFilter.Order - 1);

                        LootFilter newFilter = new LootFilter()
                        {
                            Order = 1,
                            IsActive = true,
                            Name = "New Filter",
                            Items = new List<String>(),
                            Color = new Colors()
                            {
                                R = 255,
                                G = 255,
                                B = 255,
                                A = 255
                            }
                        };

                        _config.Filters.Add(newFilter);
                        Config.SaveConfig(_config);
                        UpdateEditFilterListBox();
                    }
                }
                else
                {
                    if (MessageBox.Show("Are you sure you want to delete this filter?", "Are you sure?", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        _config.Filters.Remove(selectedFilter);

                        var lootFiltersToUpdate = _config.Filters.Select(lf => lf).Where(lf => lf.Order > selectedFilter.Order);

                        foreach (LootFilter filterToUpdate in lootFiltersToUpdate)
                        {
                            filterToUpdate.Order -= 1;
                        }

                        Config.SaveConfig(_config);
                        UpdateEditFilterListBox();
                    }
                }
            }
        }

        private void picAIScavColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("AIScav", picAIScavColor);
        }

        private void picPScavColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("PScav", picPScavColor);
        }

        private void picAIRaiderColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("AIRaider", picAIRaiderColor);
        }

        private void picBossColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("Boss", picBossColor);
        }

        private void picBEARColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("BEAR", picBEARColor);
        }

        private void picUSECColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("USEC", picUSECColor);
        }

        private void picLocalPlayerColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("LocalPlayer", picLocalPlayerColor);
        }

        private void picTeammateColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("AIScav", picTeammateColor);
        }

        private void picTeamHoverColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("TeamHover", picTeamHoverColor);
        }

        private void picRegularLootColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("RegularLoot", picRegularLootColor);
        }

        private void picImportantLootColor_Click(object sender, EventArgs e)
        {
            UpdatePaintColorByName("ImportantLoot", picImportantLootColor);
        }

        private void chkHideLootValue_CheckedChanged(object sender, EventArgs e)
        {
            _config.HideLootValue = chkHideLootValue.Checked;
        }

        private void chkShowHoverArmor_CheckedChanged(object sender, EventArgs e)
        {
            _config.ShowHoverArmor = chkShowHoverArmor.Checked;
        }
        #endregion

        #region Methods
        /// <summary>
        /// Load previously set GUI Config values. Run at startup.
        /// </summary>
        private void LoadConfig()
        {
            trkAimLength.Value = _config.PlayerAimLineLength;
            chkShowLoot.Checked = _config.LootEnabled;
            chkShowAimview.Checked = _config.AimviewEnabled;
            chkHideNames.Checked = _config.HideNames;
            chkImportantLootOnly.Checked = _config.ImportantLootOnly;
            chkHideLootValue.Checked = _config.HideLootValue;
            chkShowHoverArmor.Checked = _config.ShowHoverArmor;
            trkZoom.Value = _config.DefaultZoom;
            trkUIScale.Value = _config.UIScale;
            txtTeammateID.Text = _config.PrimaryTeammateId;
            trkRegularLootValue.Value = _config.MinLootValue / 1000;
            trkImportantLootValue.Value = _config.MinImportantLootValue / 1000;
            lblRegularLootDisplay.Text = TarkovDevAPIManager.FormatNumber(_config.MinLootValue);
            lblImportantLootDisplay.Text = TarkovDevAPIManager.FormatNumber(_config.MinImportantLootValue);

            chkNightVision.Checked = _config.NightVisionEnabled;
            chkThermalVision.Checked = _config.ThermalVisionEnabled;
            chkOpticThermalVision.Checked = _config.OpticThermalVisionEnabled;
            chkNoVisor.Checked = _config.NoVisorEnabled;

            if (_config.Filters.Count == 0)
            { // add a default, blank config
                LootFilter newFilter = new LootFilter()
                {
                    Order = 1,
                    IsActive = true,
                    Name = "Default",
                    Items = new List<String>(),
                    Color = new Colors()
                    {
                        R = 255,
                        G = 255,
                        B = 255,
                        A = 255
                    }
                };

                _config.Filters.Add(newFilter);
                Config.SaveConfig(_config);
            }

            if (cboLootItems.Items.Count == 0)
            { // add loot items loot item combobox
                List<DevLootItem> lootList = TarkovDevAPIManager.AllItems.Select(x => x.Value).OrderBy(x => x.Label).Take(25).ToList();

                cboLootItems.DataSource = lootList;
                cboLootItems.DisplayMember = "Label";
            }

            UpdateLootFilterComboBoxes();
            UpdateLootFilterList();
            UpdateEditFilterListBox();
            UpdatePaintColorControls();
        }

        /// <summary>
        /// Load map files (.PNG) and Configs (.JSON) from \\Maps folder. Run at startup.
        /// </summary>
        private void LoadMaps()
        {
            var dir = new DirectoryInfo($"{Environment.CurrentDirectory}\\Maps");
            if (!dir.Exists)
                dir.Create();

            var configs = dir.GetFiles("*.json");
            //Debug.WriteLine($"Found {configs.Length} .json map configs.");
            if (configs.Length == 0)
                throw new IOException("No .json map configs found!");

            foreach (var config in configs)
            {
                var name = Path.GetFileNameWithoutExtension(config.Name);
                //Debug.WriteLine($"Loading Map: {name}");
                var mapConfig = MapConfig.LoadFromFile(config.FullName); // Assuming LoadFromFile is updated to handle new JSON format
                //Add map ID to map config
                var mapID = mapConfig.MapID[0];
                var map = new Map(name.ToUpper(), mapConfig, config.FullName, mapID);
                // Assuming map.ConfigFile now has a 'mapLayers' property that is a List of a new type matching the JSON structure
                map.ConfigFile.MapLayers = map.ConfigFile
                    .MapLayers
                    .OrderBy(x => x.MinHeight)
                    .ToList();

                _maps.Add(map);
            }

            try
            {
                _selectedMap = _maps[0];
                _loadedBitmaps = new SKBitmap[_selectedMap.ConfigFile.MapLayers.Count];

                for (int i = 0; i < _loadedBitmaps.Length; i++)
                {
                    using (
                        var stream = File.Open(
                            _selectedMap.ConfigFile.MapLayers[i].Filename,
                            FileMode.Open,
                            FileAccess.Read))
                    {
                        _loadedBitmaps[i] = SKBitmap.Decode(stream);
                    }
                }

                tabRadar.Text = $"Radar ({_selectedMap.Name})";
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR loading initial map: {ex}");
            }
        }

        /// <summary>
        /// Zooms the bitmap 'in'.
        /// </summary>
        private void ZoomIn(int amt)
        {
            trkZoom.Value = (trkZoom.Value - amt >= 1) ? trkZoom.Value -= amt : 1;
        }

        /// <summary>
        /// Zooms the bitmap 'out'.
        /// </summary>
        private void ZoomOut(int amt)
        {
            trkZoom.Value = (trkZoom.Value + amt <= 200) ? trkZoom.Value += amt : 200;
        }

        /// <summary>
        /// Provides miscellaneous map parameters used throughout the entire render.
        /// </summary>
        private MapParameters GetMapParameters(MapPosition localPlayerPos)
        {
            int mapLayerIndex = 0;

            for (int i = _loadedBitmaps.Length; i > 0; i--)
            {
                if (localPlayerPos.Height > _selectedMap.ConfigFile.MapLayers[i - 1].MinHeight)
                {
                    mapLayerIndex = i - 1;
                    break;
                }
            }

            var zoomWidth = _loadedBitmaps[mapLayerIndex].Width * (.01f * trkZoom.Value);
            var zoomHeight = _loadedBitmaps[mapLayerIndex].Height * (.01f * trkZoom.Value);

            var bounds = new SKRect(
                localPlayerPos.X - zoomWidth / 2,
                localPlayerPos.Y - zoomHeight / 2,
                localPlayerPos.X + zoomWidth / 2,
                localPlayerPos.Y + zoomHeight / 2
            ).AspectFill(_mapCanvas.CanvasSize);

            return new MapParameters()
            {
                UIScale = _uiScale,
                MapLayerIndex = mapLayerIndex,
                Bounds = bounds,
                XScale = (float)_mapCanvas.Width / (float)bounds.Width, // Set scale for this frame
                YScale = (float)_mapCanvas.Height / (float)bounds.Height // Set scale for this frame
            };
        }

        /// <summary>
        /// Determines if an aggressor player is facing a friendly player.
        /// </summary>
        private static bool IsAggressorFacingTarget(
            SKPoint aggressor,
            float aggressorDegrees,
            SKPoint target,
            float distance)
        {
            double maxDiff = 31.3573 - 3.51726 * Math.Log(Math.Abs(0.626957 - 15.6948 * distance)); // Max degrees variance based on distance variable
            if (maxDiff < 1f)
                maxDiff = 1f; // Non linear equation, handle low/negative results

            var radians = Math.Atan2(target.Y - aggressor.Y, target.X - aggressor.X); // radians
            var degs = radians.ToDegrees();

            if (degs < 0)
                degs += 360f; // handle if negative

            var diff = Math.Abs(degs - aggressorDegrees); // Get angular difference (in degrees)
            return diff <= maxDiff; // See if calculated degrees is within max difference
        }

        /// <summary>
        /// Toggles currently selected map.
        /// </summary>
        private void ToggleMap()
        {
            if (!btnToggleMap.Enabled)
                return;
            if (_mapSelectionIndex == _maps.Count - 1)
                _mapSelectionIndex = 0; // Start over when end of maps reached
            else
                _mapSelectionIndex++; // Move onto next map
            tabRadar.Text = $"Radar ({_maps[_mapSelectionIndex].Name})";
            _mapChangeTimer.Restart(); // Start delay
        }

        /// <summary>
        /// Returns proper label for Item.
        /// </summary>
        private string GetItemLabel(DevLootItem item)
        {
            var itemValue = (chkHideLootValue.Checked ? "" : $"[{TarkovDevAPIManager.FormatNumber(TarkovDevAPIManager.GetItemValue(item.Item))}] ");
            return (item.AlwaysShow || item.Item.shortName is not null) ? $"{itemValue}{item.Item.shortName}" : "null";
        }

        /// <summary>
        /// Adds filters into the combo box for selection
        /// </summary>
        private void UpdateLootFilterComboBoxes()
        {
            cboFilters.DataSource = null;
            cboFilters.Items.Clear();

            List<LootFilter> lootFilters = _config.Filters.OrderBy(lf => lf.Order).ToList();

            foreach (LootFilter filter in lootFilters)
            {
                cboFilters.Items.Add(filter);
            }

            cboFilters.DisplayMember = "Name";

            if (lootFilters.Count > 0)
            {
                cboFilters.SelectedItem = lootFilters[0];
            }
        }

        /// <summary>
        /// Adds items from the loot filter into the list view
        /// </summary>
        private void UpdateLootFilterList()
        {
            lstViewLootFilter.Items.Clear();

            LootFilter selectedFilter = (LootFilter)cboFilters.SelectedItem;
            List<DevLootItem> lootList = TarkovDevAPIManager.AllItems.Select(x => x.Value).ToList();
            if (selectedFilter != null)
            {
                List<DevLootItem> matchingLoot = lootList.Where(loot => selectedFilter.Items.Any(id => id == loot.Item.id)).OrderBy(l => l.Item.name).ToList();

                foreach (DevLootItem item in matchingLoot)
                {
                    ListViewItem listItem = new ListViewItem()
                    {
                        Text = item.Item.id,
                        Tag = item
                    };

                    listItem.SubItems.Add(item.Item.name);
                    listItem.SubItems.Add(item.Item.shortName);
                    listItem.SubItems.Add(TarkovDevAPIManager.FormatNumber(TarkovDevAPIManager.GetItemValue(item.Item)));

                    lstViewLootFilter.Items.Add(listItem);
                }

                chkLootFilterActive.Checked = selectedFilter.IsActive;
            }
        }

        /// <summary>
        /// Checks if the text, IsActive or color is different
        /// </summary>
        /// <returns>A bool</returns>
        private bool HasUnsavedFilterEditChanges()
        {
            LootFilter selectedFilter = (LootFilter)lstEditLootFilters.SelectedItem;

            if (selectedFilter != null)
            {
                Colors col = selectedFilter.Color;

                return txtLootFilterEditName.Text != selectedFilter.Name ||
                        chkLootFilterEditActive.Checked != selectedFilter.IsActive ||
                        picLootFilterEditColor.BackColor.ToArgb() != Color.FromArgb(col.A, col.R, col.G, col.B).ToArgb();
            }
            else
            {
                return false;
            }
        }

        private void UpdatePaintColorControls()
        {
            var colors = _config.PaintColors;

            Action<PictureBox, string> setColor = (pictureBox, name) =>
            {
                if (colors.ContainsKey(name))
                {
                    PaintColor.Colors color = colors[name];
                    pictureBox.BackColor = Color.FromArgb(color.A, color.R, color.G, color.B);
                }
                else
                {
                    pictureBox.BackColor = Color.FromArgb(255, 255, 255, 255);
                }
            };

            setColor(picAIScavColor, "AIScav");
            setColor(picPScavColor, "PScav");
            setColor(picAIRaiderColor, "AIRaider");
            setColor(picBossColor, "Boss");
            setColor(picUSECColor, "USEC");
            setColor(picBEARColor, "BEAR");
            setColor(picLocalPlayerColor, "LocalPlayer");
            setColor(picTeammateColor, "Teammate");
            setColor(picTeamHoverColor, "TeamHover");
            setColor(picRegularLootColor, "RegularLoot");
            setColor(picImportantLootColor, "ImportantLoot");
        }

        /// <summary>
        /// Updates the Color of a PaintColor object in the PaintColors dictionary by name
        /// </summary>
        private void UpdatePaintColorByName(string name, PictureBox pictureBox)
        {
            if (colDialog.ShowDialog() == DialogResult.OK)
            {
                Color col = colDialog.Color;
                pictureBox.BackColor = col;

                _config.PaintColors[name] = new PaintColor.Colors
                {
                    A = col.A,
                    R = col.R,
                    G = col.G,
                    B = col.B
                };
            }
        }
        #endregion

        #region Render
        /// <summary>
        /// Main Render Event.
        /// </summary>
        private void MapCanvas_PaintSurface(object sender, SKPaintGLSurfaceEventArgs e)
        {
            //Debug.WriteLine("MapCanvas_PaintSurface: Method called");
            //Debug.WriteLine("MapCanvas_PaintSurface: Method called");
            lock (_renderLock) // Acquire lock on 'Render Resources'
            {
                bool isReady = this.Ready; // cache bool
                bool inGame = this.InGame; // cache bool
                bool isAtHideout = this.IsAtHideout; // cache bool
                string currentMap = this.CurrentMapName; // cache string
                var localPlayer = this.LocalPlayer; // cache ref to current player

                if (_fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    _mapCanvas.GRContext.PurgeResources(); // Seems to fix mem leak issue on increasing resource cache
                    string title = "EFT Radar";
                    if (inGame && localPlayer is not null)
                    {
                        // Check if map changed
                        if (currentMap.ToLower() != _selectedMap.MapID.ToLower())
                        {
                            _selectedMap = _maps.FirstOrDefault(x => x.MapID.ToLower() == currentMap.ToLower());

                            if (currentMap.ToLower() == "factory4_night")
                            {
                                _selectedMap = _maps.FirstOrDefault(x => x.MapID.ToLower() == "factory4_night");
                                _selectedMap = _maps[1];
                            }

                            if (_selectedMap is null)
                            {
                                _selectedMap = _maps[0];
                            }
                            //init map
                            if (_loadedBitmaps is not null)
                            {
                                foreach (var bitmap in _loadedBitmaps)
                                    bitmap?.Dispose(); // Cleanup resources
                            }

                            _loadedBitmaps = new SKBitmap[_selectedMap.ConfigFile.MapLayers.Count];
                            for (int i = 0; i < _loadedBitmaps.Length; i++)
                            {
                                using (
                                    var stream = File.Open(
                                        _selectedMap.ConfigFile.MapLayers[i].Filename,
                                        FileMode.Open,
                                        FileAccess.Read))
                                {
                                    _loadedBitmaps[i] = SKBitmap.Decode(stream); // Load new bitmap(s)
                                }
                            }
                            tabRadar.Text = $"Radar ({_selectedMap.Name})";
                        }
                        else if (_selectedMap is null)
                        {
                            _selectedMap = _maps[0];
                            //init map
                            if (_loadedBitmaps is not null)
                            {
                                foreach (var bitmap in _loadedBitmaps)
                                    bitmap?.Dispose(); // Cleanup resources
                            }

                            _loadedBitmaps = new SKBitmap[_selectedMap.ConfigFile.MapLayers.Count];
                            for (int i = 0; i < _loadedBitmaps.Length; i++)
                            {
                                using (
                                    var stream = File.Open(
                                        _selectedMap.ConfigFile.MapLayers[i].Filename,
                                        FileMode.Open,
                                        FileAccess.Read))
                                {
                                    _loadedBitmaps[i] = SKBitmap.Decode(stream); // Load new bitmap(s)
                                }
                            }
                            tabRadar.Text = $"Radar ({_selectedMap.Name})";
                        }

                        title += $" ({_fps} fps) ({Memory.Ticks} mem/s)";

                        if (this.LoadingLoot)
                            title += " - LOADING LOOT";
                    }

                    this.Text = title; // Set window title
                    _fpsWatch.Restart();
                    _fps = 0;
                }
                else
                    _fps++;

                SKSurface surface = e.Surface;
                SKCanvas canvas = surface.Canvas;
                canvas.Clear();

                try
                {
                    if (inGame && localPlayer is not null)
                    {
                        var closestPlayerToMouse = _closestPlayerToMouse; // cache ref
                        var closestItemToMouse = _closestItemToMouse; // cache ref
                        var mouseOverGrp = _mouseOverGroup; // cache value for entire render
                        // Get main player location
                        var localPlayerPos = localPlayer.Position;
                        var localPlayerMapPos = localPlayerPos.ToMapPos(_selectedMap);

                        if (grpMapSetup.Visible) // Print coordinates (to make it easy to setup JSON configs)
                        {
                            lblMapCoords.Text = $"Unity X,Y,Z: {localPlayerPos.X},{localPlayerPos.Y},{localPlayerPos.Z}";
                        }

                        // Prepare to draw Game Map
                        MapParameters mapParams; // Drawing Source
                        if (chkMapFree.Checked) // Map fixed location, click to pan map
                        {
                            _mapPanPosition.Height = localPlayerMapPos.Height;
                            mapParams = GetMapParameters(_mapPanPosition);
                        }
                        else
                            mapParams = GetMapParameters(localPlayerMapPos); // Map auto follow LocalPlayer

                        var mapCanvasBounds = new SKRect() // Drawing Destination
                        {
                            Left = _mapCanvas.Left,
                            Right = _mapCanvas.Right,
                            Top = _mapCanvas.Top,
                            Bottom = _mapCanvas.Bottom
                        };

                        // Draw Game Map
                        canvas.DrawBitmap(
                            _loadedBitmaps[mapParams.MapLayerIndex],
                            mapParams.Bounds,
                            mapCanvasBounds,
                            SKPaints.PaintBitmap
                        );

                        // Draw LocalPlayer Scope
                        {
                            var localPlayerZoomedPos = localPlayerMapPos.ToZoomedPos(mapParams); // always true
                            localPlayerZoomedPos.DrawPlayerMarker(
                                canvas,
                                localPlayer,
                                trkAimLength.Value,
                                null
                            );
                        }

                        // Draw other players
                        var allPlayers = this.AllPlayers
                            ?.Select(x => x.Value)
                            .Where(x => !x.HasExfild); // Skip exfil'd players

                        var friendlies = allPlayers?.Where(x => x.IsFriendlyActive);

                        if (allPlayers is not null)
                        {
                            foreach (var player in allPlayers) // Draw PMCs
                            {
                                if (player.Type == PlayerType.LocalPlayer)
                                    continue; // Already drawn current player, move on

                                var playerPos = player.Position;
                                var playerMapPos = playerPos.ToMapPos(_selectedMap);
                                var playerZoomedPos = playerMapPos.ToZoomedPos(mapParams);

                                player.ZoomedPosition = new Vector2() // Cache Position as Vec2 for MouseMove event
                                {
                                    X = playerZoomedPos.X,
                                    Y = playerZoomedPos.Y
                                };

                                int aimlineLength = 15;

                                if (!player.IsAlive)
                                { // Draw 'X' death marker
                                    playerZoomedPos.DrawDeathMarker(canvas);
                                    continue;

                                }
                                else if (player.Type is not PlayerType.Teammate)
                                {
                                    if (friendlies is not null)
                                        foreach (var friendly in friendlies)
                                        {
                                            var friendlyPos = friendly.Position;
                                            var friendlyDist = Vector3.Distance(
                                                playerPos,
                                                friendlyPos
                                            );

                                            if (friendlyDist > _config.MaxDistance)
                                                continue; // max range, no lines across entire map

                                            var friendlyMapPos = friendlyPos.ToMapPos(_selectedMap);

                                            if (IsAggressorFacingTarget(
                                                    playerMapPos.GetPoint(),
                                                    player.Rotation.X,
                                                    friendlyMapPos.GetPoint(),
                                                    friendlyDist))
                                            {
                                                aimlineLength = 1000; // Lengthen aimline
                                                break;
                                            }
                                        }
                                }
                                else if (player.Type is PlayerType.Teammate)
                                {
                                    aimlineLength = trkAimLength.Value; // Allies use player's aim length
                                }
                                // Draw Player Scope
                                {
                                    string[] lines = null;
                                    var height = playerMapPos.Height - localPlayerMapPos.Height;
                                    var dist = Vector3.Distance(localPlayerPos, playerPos);

                                    if (!chkHideNames.Checked) // show full names & info
                                    {
                                        lines = new string[2]
                                        {
                                            string.Empty,
                                            $"{(int)Math.Round(height)}, {(int)Math.Round(dist)}"
                                        };

                                        string name = player.Name;

                                        if (player.ErrorCount > 10)
                                            name = "ERROR"; // In case POS stops updating, let us know!

                                        if (player.IsHuman || player.IsBossRaider)
                                            lines[0] += $"{name} ({player.Health})";
                                        else
                                            lines[0] += $"{name}";
                                    }
                                    else // just height & hp (for humans)
                                    {
                                        lines = new string[1] { $"{(int)Math.Round(height)}, {(int)Math.Round(dist)}" };

                                        if (player.IsHuman || player.IsBossRaider)
                                            lines[0] += $" ({player.Health})";
                                        if (player.ErrorCount > 10)
                                            lines[0] = "ERROR"; // In case POS stops updating, let us know!
                                    }

                                    playerZoomedPos.DrawPlayerText(
                                        canvas,
                                        player,
                                        lines,
                                        mouseOverGrp
                                    );

                                    playerZoomedPos.DrawPlayerMarker(
                                        canvas,
                                        player,
                                        aimlineLength,
                                        mouseOverGrp
                                    );
                                }
                            }

                            if (chkShowLoot.Checked) // Draw loot (if enabled)
                            {
                                var loot = this.Loot; // cache ref
                                //Debug.WriteLine($"Loot is null: {loot is null}");
                                if (loot is not null)
                                {
                                    if (Loot.Filter is null)
                                    {
                                        Loot.ApplyFilter();
                                    }

                                    var filter = Loot.Filter; // Get ref to collection

                                    if (filter is not null)
                                        foreach (var item in filter)
                                        {
                                            float position = item.Position.Z - localPlayerMapPos.Height;

                                            if (chkImportantLootOnly.Checked && !item.Important)
                                            {
                                                continue;
                                            }

                                            var itemZoomedPos = item.Position
                                                                    .ToMapPos(_selectedMap)
                                                                    .ToZoomedPos(mapParams);

                                            item.ZoomedPosition = new Vector2() // Cache Position as Vec2 for MouseMove event
                                            {
                                                X = itemZoomedPos.X,
                                                Y = itemZoomedPos.Y
                                            };

                                            itemZoomedPos.DrawLoot(
                                                canvas,
                                                item,
                                                position
                                            );
                                        }

                                    // coprses = ootItem {Position = pos,AlwaysShow = true,Label = "Corpse"
                                }
                            }

                            var grenades = this.Grenades; // cache ref
                            if (grenades is not null) // Draw grenades
                            {
                                foreach (var grenade in grenades)
                                {
                                    var grenadeZoomedPos = grenade
                                        .Position
                                        .ToMapPos(_selectedMap)
                                        .ToZoomedPos(mapParams);
                                    grenadeZoomedPos.DrawGrenade(canvas);
                                }
                            }

                            var exfils = this.Exfils; // cache ref
                            if (exfils is not null)
                            {
                                foreach (var exfil in exfils)
                                {
                                    var exfilZoomedPos = exfil
                                        .Position
                                        .ToMapPos(_selectedMap)
                                        .ToZoomedPos(mapParams);

                                    exfilZoomedPos.DrawExfil(
                                        canvas,
                                        exfil,
                                        localPlayerMapPos.Height
                                    );
                                }
                            }
                        }

                        if (chkShowAimview.Checked) // Aimview Drawing
                        {
                            var aimviewPlayers = allPlayers?.Where(x => x.IsActive && x.IsAlive); // get all alive & active players
                            if (aimviewPlayers is not null)
                            {
                                var localPlayerAimviewBounds = new SKRect() // bottom left of screen
                                {
                                    Left = _mapCanvas.Left,
                                    Right = _mapCanvas.Left + _aimviewWindowSize,
                                    Bottom = _mapCanvas.Bottom,
                                    Top = _mapCanvas.Bottom - _aimviewWindowSize
                                };

                                var primaryTeammateAimviewBounds = new SKRect() // bottom right of screen
                                {
                                    Left = _mapCanvas.Right - _aimviewWindowSize,
                                    Right = _mapCanvas.Right,
                                    Bottom = _mapCanvas.Bottom,
                                    Top = _mapCanvas.Bottom - _aimviewWindowSize
                                };

                                var primaryTeammate = friendlies?.FirstOrDefault(
                                    x => x.AccountID == txtTeammateID.Text
                                ); // Find Primary Teammate

                                // Draw LocalPlayer Aimview
                                RenderAimview(
                                    canvas,
                                    localPlayerAimviewBounds,
                                    localPlayer,
                                    aimviewPlayers
                                );

                                // Draw Primary Teammate Aimview
                                RenderAimview(
                                    canvas,
                                    primaryTeammateAimviewBounds,
                                    primaryTeammate,
                                    aimviewPlayers
                                );
                            }
                        }

                        if (closestItemToMouse is not null) // draw tooltip for item the mouse is closest to
                        {
                            var itemZoomedPos = closestItemToMouse
                                .Position
                                .ToMapPos(_selectedMap)
                                .ToZoomedPos(mapParams);
                            itemZoomedPos.DrawContainerTooltip(canvas, closestItemToMouse);
                        }

                        if (closestPlayerToMouse is not null) // draw tooltip for player the mouse is closest to
                        {
                            var playerZoomedPos = closestPlayerToMouse
                                .Position
                                .ToMapPos(_selectedMap)
                                .ToZoomedPos(mapParams);
                            playerZoomedPos.DrawToolTip(canvas, closestPlayerToMouse);
                        }
                    }
                    else // Not rendering, display reason
                    {
                        if (!isReady)
                            canvas.DrawText(
                                "Game Process Not Running",
                                _mapCanvas.Width / 2,
                                _mapCanvas.Height / 2,
                                SKPaints.TextRadarStatus
                            );
                        else if (IsAtHideout)
                            canvas.DrawText(
                                "Main Menu or Hideout...",
                                _mapCanvas.Width / 2,
                                _mapCanvas.Height / 2,
                                SKPaints.TextRadarStatus
                            );
                        else if (!inGame)
                            canvas.DrawText(
                                "Waiting for Raid Start...",
                                _mapCanvas.Width / 2,
                                _mapCanvas.Height / 2,
                                SKPaints.TextRadarStatus
                            );
                        //loading loot check
                        else if (LoadingLoot)
                            canvas.DrawText(
                                "Loading Loot...",
                                _mapCanvas.Width / 2,
                                _mapCanvas.Height / 2,
                                SKPaints.TextRadarStatus
                            );
                        else if (localPlayer is null)
                            canvas.DrawText(
                                "Cannot find LocalPlayer",
                                _mapCanvas.Width / 2,
                                _mapCanvas.Height / 2,
                                SKPaints.TextRadarStatus
                            );
                    }
                }
                catch { }
                canvas.Flush(); // commit to GPU
            }
        }

        /// <summary>
        /// Renders an Aimview Window with the specified parameters.
        /// </summary>
        /// <param name="canvas">SKCanvas reference for drawing.</param>
        /// <param name="drawingLocation">Rectangular (Square) location on the SKCanvas to draw.</param>
        /// <param name="sourcePlayer">The player whom the Aimview will have 'point of view'.</param>
        /// <param name="aimviewPlayers">Collection of players to render in the AimView window.</param>
        private void RenderAimview(
            SKCanvas canvas,
            SKRect drawingLocation,
            Player sourcePlayer,
            IEnumerable<Player> aimviewPlayers)
        {
            try
            {
                if (sourcePlayer is not null && sourcePlayer.IsActive && sourcePlayer.IsAlive)
                {
                    var myPosition = sourcePlayer.Position;
                    var myRotation = sourcePlayer.Rotation;
                    canvas.DrawRect(drawingLocation, SKPaints.PaintTransparentBacker); // draw backer

                    if (aimviewPlayers is not null)
                    {
                        var normalizedDirection = -myRotation.X;

                        if (normalizedDirection < 0)
                            normalizedDirection += 360;

                        var pitch = myRotation.Y;
                        if (pitch >= 270)
                        {
                            pitch = 360 - pitch;
                        }
                        else
                        {
                            pitch = -pitch;
                        }

                        foreach (var player in aimviewPlayers)
                        {
                            if (player == sourcePlayer)
                                continue; // don't draw self

                            var playerPos = player.Position;
                            float dist = Vector3.Distance(myPosition, playerPos);

                            if (dist > _config.MaxDistance)
                                continue; // Only draw within range

                            float heightDiff = playerPos.Z - myPosition.Z;
                            float angleY =
                                (float)(180 / Math.PI * Math.Atan(heightDiff / dist)) - pitch;
                            float y =
                                angleY / _config.AimViewFOV * _aimviewWindowSize
                                + _aimviewWindowSize / 2;

                            float opposite = playerPos.Y - myPosition.Y;
                            float adjacent = playerPos.X - myPosition.X;
                            float angleX = (float)(180 / Math.PI * Math.Atan(opposite / adjacent));

                            if (adjacent < 0 && opposite > 0)
                            {
                                angleX += 180;
                            }
                            else if (adjacent < 0 && opposite < 0)
                            {
                                angleX += 180;
                            }
                            else if (adjacent > 0 && opposite < 0)
                            {
                                angleX += 360;
                            }
                            // Handle split planes (source/target each on a different side of 0 / 360 )
                            if (angleX >= 360 - _config.AimViewFOV && normalizedDirection <= _config.AimViewFOV)
                            {
                                var diff = 360 + normalizedDirection;
                                angleX -= diff;
                            }
                            else if (angleX <= _config.AimViewFOV && normalizedDirection >= 360 - _config.AimViewFOV)
                            {
                                var diff = 360 - normalizedDirection;
                                angleX += diff;
                            }
                            else
                                angleX -= normalizedDirection;

                            float x = angleX / _config.AimViewFOV * _aimviewWindowSize + _aimviewWindowSize / 2;
                            float drawX = drawingLocation.Right - x;
                            float drawY = drawingLocation.Bottom - y;
                            if (drawX > drawingLocation.Right
                                || drawX < drawingLocation.Left
                                || drawY < drawingLocation.Top
                                || drawY > drawingLocation.Bottom
                                )
                                continue; // not in FOV

                            float circleSize = (float)(
                                31.6437 - 5.09664 * Math.Log(0.591394 * dist + 70.0756)
                            );

                            canvas.DrawCircle(
                                drawX,
                                drawY,
                                circleSize * _uiScale,
                                player.GetAimviewPaint()
                            );
                        }
                    }
                    // draw crosshair at end
                    canvas.DrawLine(
                        drawingLocation.Left,
                        drawingLocation.Bottom - (_aimviewWindowSize / 2),
                        drawingLocation.Right,
                        drawingLocation.Bottom - (_aimviewWindowSize / 2),
                        SKPaints.PaintAimviewCrosshair
                    );

                    canvas.DrawLine(
                        drawingLocation.Right - (_aimviewWindowSize / 2),
                        drawingLocation.Top,
                        drawingLocation.Right - (_aimviewWindowSize / 2),
                        drawingLocation.Bottom,
                        SKPaints.PaintAimviewCrosshair
                    );
                }
            }
            catch { }
        }
        #endregion

        #region Overrides
        /// <summary>
        /// Form closing event.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = true; // Cancel shutdown
            this.Enabled = false; // Lock window
            _config.PlayerAimLineLength = trkAimLength.Value;
            _config.LootEnabled = chkShowLoot.Checked;
            _config.AimviewEnabled = chkShowAimview.Checked;
            _config.HideNames = chkHideNames.Checked;
            _config.ImportantLootOnly = chkImportantLootOnly.Checked;
            _config.HideLootValue = chkHideLootValue.Checked;
            _config.DefaultZoom = trkZoom.Value;
            _config.UIScale = trkUIScale.Value;
            _config.PrimaryTeammateId = txtTeammateID.Text;

            _config.ThermalVisionEnabled = chkThermalVision.Checked;
            _config.NightVisionEnabled = chkNightVision.Checked;
            _config.NoVisorEnabled = chkNoVisor.Checked;
            _config.OpticThermalVisionEnabled = chkOpticThermalVision.Checked;

            Config.SaveConfig(_config); // Save Config to Config.json
            Memory.Shutdown(); // Wait for Memory Thread to gracefully exit
            e.Cancel = false; // Ready to close
            base.OnFormClosing(e); // Proceed with closing
        }

        /// <summary>
        /// Process hotkey presses.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F1:
                    ZoomIn(5);
                    return true;
                case Keys.F2:
                    ZoomOut(5);
                    return true;
                case Keys.F3:
                    this.chkShowLoot.Checked = !this.chkShowLoot.Checked; // Toggle loot
                    return true;
                case Keys.F4:
                    this.chkShowAimview.Checked = !this.chkShowAimview.Checked; // Toggle aimview
                    return true;
                case Keys.F5:
                    ToggleMap(); // Toggle to next map
                    return true;
                case Keys.F6:
                    chkHideNames.Checked = !chkHideNames.Checked; // Toggle Hide Names
                    return true;
                // Night Vision Ctrl + N
                case Keys.Control | Keys.N:
                    chkNightVision.Checked = !chkNightVision.Checked;
                    return true;
                // Thermal Vision Ctrl + T
                case Keys.Control | Keys.T:
                    chkThermalVision.Checked = !chkThermalVision.Checked;
                    return true;
                default:
                    return base.ProcessCmdKey(ref msg, keyData);
            }
        }

        /// <summary>
        /// Process mousewheel events.
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (tabControl.SelectedIndex == 0) // Main Radar Tab should be open
            {
                bool increment = e.Delta > 0;
                int amt = (e.Delta / (increment ? SystemInformation.MouseWheelScrollDelta : -SystemInformation.MouseWheelScrollDelta)) * 5; // Calculate zoom amount based on number of deltas

                if (increment)
                {
                    ZoomIn(amt);
                }
                else
                {
                    ZoomOut(amt);
                }

                return;
            }

            base.OnMouseWheel(e);
        }
    }
    #endregion
}