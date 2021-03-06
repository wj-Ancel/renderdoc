﻿/******************************************************************************
 * The MIT License (MIT)
 * 
 * Copyright (c) 2014 Crytek
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 ******************************************************************************/


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using renderdocui.Code;
using renderdocui.Controls;
using renderdoc;
using System.Threading;

namespace renderdocui.Windows
{
    public partial class TextureViewer : DockContent, ILogViewerForm
    {
        #region Privates

        private Core m_Core;

        private ReplayOutput m_Output = null;

        private TextureDisplay m_TexDisplay = new TextureDisplay();

        private ToolStripControlHost depthStencilToolstrip = null;

        private DockContent m_PreviewPanel = null;
        private DockContent m_TexlistDockPanel = null;

        private FileSystemWatcher m_FSWatcher = null;

        public enum FollowType { RT_UAV, Depth, PSResource }
        struct Following
        {
            public FollowType Type;
            public int index;

            public Following(FollowType t, int i) { Type = t; index = i; }

            public override int GetHashCode()
            {
                return Type.GetHashCode() + index.GetHashCode();
            }
            public override bool Equals(object obj)
            {
                return obj is Following && this == (Following)obj;
            }
            public static bool operator ==(Following s1, Following s2)
            {
                return s1.Type == s2.Type && s1.index == s2.index;
            }
            public static bool operator !=(Following s1, Following s2)
            {
                return !(s1 == s2);
            }

            public UInt32 GetFirstArraySlice(Core core)
            {
                // todo, implement this better for GL :(

                if (core.APIProps.pipelineType == APIPipelineStateType.D3D11)
                {
                    D3D11PipelineState.ShaderStage.ResourceView view = null;

                    if (Type == FollowType.RT_UAV)
                    {
                        view = core.CurD3D11PipelineState.m_OM.RenderTargets[index];

                        if (view.Resource == ResourceId.Null && index >= core.CurD3D11PipelineState.m_OM.UAVStartSlot)
                            view = core.CurD3D11PipelineState.m_OM.UAVs[index - core.CurD3D11PipelineState.m_OM.UAVStartSlot];
                    }
                    else if (Type == FollowType.Depth)
                    {
                        view = core.CurD3D11PipelineState.m_OM.DepthTarget;
                    }
                    else if (Type == FollowType.PSResource)
                    {
                        view = core.CurD3D11PipelineState.m_PS.SRVs[index];
                    }

                    return view != null ? view.FirstArraySlice : 0;
                }
                else
                {
                    if (Type == FollowType.PSResource)
                    {
                        return core.CurGLPipelineState.Textures[index].FirstSlice;
                    }
                }

                return 0;
            }

            public ResourceId GetResourceId(Core core)
            {
                ResourceId id = ResourceId.Null;

                if (Type == FollowType.RT_UAV)
                {
                    var outputs = core.CurPipelineState.GetOutputTargets();
                    if(outputs.Length > index)
                        id = outputs[index];
                }
                else if (Type == FollowType.Depth)
                {
                    id = core.CurPipelineState.OutputDepth;
                }
                else if (Type == FollowType.PSResource)
                {
                    var res = core.CurPipelineState.GetResources(ShaderStageType.Pixel);
                    if(res.Length > index)
                        id = res[index];
                }

                return id;
            }
        }
        private Following m_Following = new Following(FollowType.RT_UAV, 0);

        #endregion

        public TextureViewer(Core core)
        {
            m_Core = core;

            InitializeComponent();

            Icon = global::renderdocui.Properties.Resources.icon;

            textureList.m_Core = core;
            textureList.GoIconClick += new EventHandler<GoIconClickEventArgs>(textureList_GoIconClick);

            UI_SetupToolstrips();
            UI_SetupDocks();
            UI_UpdateTextureDetails();
            statusLabel.Text = "";
            zoomOption.SelectedText = "";
            mipLevel.Enabled = false;
            sliceFace.Enabled = false;

            PixelPicked = false;

            mainLayout.Dock = DockStyle.Fill;

            render.Painting = true;
            pixelContext.Painting = true;

            saveTex.Enabled = false;

            DockHandler.GetPersistStringCallback = PersistString;

            renderContainer.MouseWheelHandler = render_MouseWheel;
            render.MouseWheel += render_MouseWheel;
            renderContainer.MouseDown += render_MouseClick;
            renderContainer.MouseMove += render_MouseMove;

            render.KeyHandler = render_KeyDown;

            rangeHistogram.RangeUpdated += new EventHandler<RangeHistogramEventArgs>(rangeHistogram_RangeUpdated);

            this.DoubleBuffered = true;

            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            channels.SelectedIndex = 0;

            FitToWindow = true;
            overlay.SelectedIndex = 0;
            m_Following = new Following(FollowType.RT_UAV, 0);

            texturefilter.SelectedIndex = 0;

            if (m_Core.LogLoaded)
                OnLogfileLoaded();
        }

        private void UI_SetupDocks()
        {
            m_PreviewPanel = Helpers.WrapDockContent(dockPanel, renderToolstripContainer, "Current");
            m_PreviewPanel.DockState = DockState.Document;
            m_PreviewPanel.AllowEndUserDocking = false;
            m_PreviewPanel.Show();

            m_PreviewPanel.CloseButton = false;
            m_PreviewPanel.CloseButtonVisible = false;

            m_PreviewPanel.DockHandler.TabPageContextMenuStrip = tabContextMenu;

            dockPanel.ActiveDocumentChanged += new EventHandler(dockPanel_ActiveDocumentChanged);

            var w3 = Helpers.WrapDockContent(dockPanel, texPanel, "PS Resources");
            w3.DockAreas &= ~DockAreas.Document;
            w3.DockState = DockState.DockRight;
            w3.Show();

            w3.CloseButton = false;
            w3.CloseButtonVisible = false;

            var w5 = Helpers.WrapDockContent(dockPanel, rtPanel, "OM Targets");
            w5.DockAreas &= ~DockAreas.Document;
            w5.DockState = DockState.DockRight;
            w5.Show(w3.Pane, w3);

            w5.CloseButton = false;
            w5.CloseButtonVisible = false;

            m_TexlistDockPanel = Helpers.WrapDockContent(dockPanel, texlistContainer, "Texture List");
            m_TexlistDockPanel.DockAreas &= ~DockAreas.Document;
            m_TexlistDockPanel.DockState = DockState.DockLeft;
            m_TexlistDockPanel.Hide();

            m_TexlistDockPanel.HideOnClose = true;

            var w4 = Helpers.WrapDockContent(dockPanel, pixelContextPanel, "Pixel Context");
            w4.DockAreas &= ~DockAreas.Document;
            w4.Show(w3.Pane, DockAlignment.Bottom, 0.3);

            w4.CloseButton = false;
            w4.CloseButtonVisible = false;
        }

        private void UI_SetupToolstrips()
        {
            int idx = rangeStrip.Items.IndexOf(rangeWhite);
            rangeStrip.Items.Insert(idx, new ToolStripControlHost(rangeHistogram));

            for (int i = 0; i < channelStrip.Items.Count; i++)
            {
                if (channelStrip.Items[i] == mulSep)
                {
                    depthStencilToolstrip = new ToolStripControlHost(depthstencilPanel);
                    channelStrip.Items.Insert(i, depthStencilToolstrip);
                    break;
                }
            }
        }

        public class PersistData
        {
            public static int currentPersistVersion = 4;
            public int persistVersion = currentPersistVersion;

            public string panelLayout;

            public static PersistData GetDefaults()
            {
                PersistData data = new PersistData();

                data.panelLayout = "";

                return data;
            }
        }

        public void InitFromPersistString(string str)
        {
            PersistData data = null;

            try
            {
                if (str.Length > GetType().ToString().Length)
                {
                    var reader = new StringReader(str.Substring(GetType().ToString().Length));

                    System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(PersistData));
                    data = (PersistData)xs.Deserialize(reader);

                    reader.Close();
                }
            }
            catch (System.Xml.XmlException)
            {
            }
            catch(InvalidOperationException)
            {
                // don't need to handle it. Leave data null and pick up defaults below
            }

            if (data == null || data.persistVersion != PersistData.currentPersistVersion)
            {
                data = PersistData.GetDefaults();
            }

            ApplyPersistData(data);
        }

        private IDockContent GetContentFromPersistString(string persistString)
        {
            Control[] persistors = {
                                       renderToolstripContainer,
                                       texPanel,
                                       rtPanel,
                                       texlistContainer,
                                       pixelContextPanel
                                   };

            foreach(var p in persistors)
                if (persistString == p.Name && p.Parent is IDockContent && (p.Parent as DockContent).DockPanel == null)
                    return p.Parent as IDockContent;

            return null;
        }

        private string onloadLayout = "";

        private void ApplyPersistData(PersistData data)
        {
            onloadLayout = data.panelLayout;
        }

        private void TextureViewer_Load(object sender, EventArgs e)
        {
            if (onloadLayout != "")
            {
                Control[] persistors = {
                                       renderToolstripContainer,
                                       texPanel,
                                       rtPanel,
                                       texlistContainer,
                                       pixelContextPanel
                                   };

                foreach (var p in persistors)
                    (p.Parent as DockContent).DockPanel = null;

                var enc = new UnicodeEncoding();
                using (var strm = new MemoryStream(enc.GetBytes(onloadLayout)))
                {
                    strm.Flush();
                    strm.Position = 0;

                    dockPanel.LoadFromXml(strm, new DeserializeDockContent(GetContentFromPersistString));
                }

                onloadLayout = "";
            }
        }

        private string PersistString()
        {
            var writer = new StringWriter();

            writer.Write(GetType().ToString());

            PersistData data = new PersistData();

            // passing in a MemoryStream gets disposed - can't see a way to retrieve this
            // in-memory.
            var enc = new UnicodeEncoding();
            var path = Path.GetTempFileName();
            dockPanel.SaveAsXml(path, "", enc);
            data.panelLayout = File.ReadAllText(path, enc);
            File.Delete(path);

            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(PersistData));
            xs.Serialize(writer, data);

            return writer.ToString();
        }

        private bool DisableThumbnails { get { return m_Core != null && m_Core.Config != null && 
                                                      m_Core.Config.TextureViewer_DisableThumbnails; } }

        #region Public Functions

        private Dictionary<ResourceId, DockContent> lockedTabs = new Dictionary<ResourceId, DockContent>();

        public void ViewTexture(ResourceId ID, bool focus)
        {
            TextureViewer_Load(null, null);

            if (lockedTabs.ContainsKey(ID))
            {
                if (!lockedTabs[ID].IsDisposed && !lockedTabs[ID].IsHidden)
                {
                    if (focus)
                        Show();

                    lockedTabs[ID].Show();
                    m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
                    return;
                }

                lockedTabs.Remove(ID);
            }

            for (int i = 0; i < m_Core.CurTextures.Length; i++)
            {
                if (m_Core.CurTextures[i].ID == ID)
                {
                    FetchTexture current = m_Core.CurTextures[i];

                    var newPanel = Helpers.WrapDockContent(dockPanel, renderToolstripContainer, current.name);

                    newPanel.DockState = DockState.Document;
                    newPanel.AllowEndUserDocking = false;

                    newPanel.Icon = Icon.FromHandle(global::renderdocui.Properties.Resources.page_white_link.GetHicon());

                    newPanel.Tag = current;

                    newPanel.DockHandler.TabPageContextMenuStrip = tabContextMenu;
                    newPanel.FormClosing += new FormClosingEventHandler(PreviewPanel_FormClosing);

                    newPanel.Show(m_PreviewPanel.Pane, null);

                    newPanel.Show();

                    if (focus)
                        Show();

                    lockedTabs.Add(ID, newPanel);

                    m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
                    return;
                }
            }

            for (int i = 0; i < m_Core.CurBuffers.Length; i++)
            {
                if (m_Core.CurBuffers[i].ID == ID)
                {
                    var viewer = new BufferViewer(m_Core, false);
                    viewer.ViewRawBuffer(ID);
                    viewer.Show(DockPanel);
                    return;
                }
            }
        }

        #endregion

        #region Custom Shader handling

        private List<string> m_CustomShadersBusy = new List<string>();
        private Dictionary<string, ResourceId> m_CustomShaders = new Dictionary<string, ResourceId>();
        private Dictionary<string, ShaderViewer> m_CustomShaderEditor = new Dictionary<string, ShaderViewer>();

        private void ReloadCustomShaders(string filter)
        {
            if (!m_Core.LogLoaded) return;

            if (filter == "")
            {
                var shaders = m_CustomShaders.Values.ToArray();

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    foreach (var s in shaders)
                        r.FreeCustomShader(s);
                });

                customShader.Items.Clear();
                m_CustomShaders.Clear();
            }
            else
            {
                var fn = Path.GetFileNameWithoutExtension(filter);
                var key = fn.ToLowerInvariant();

                if (m_CustomShaders.ContainsKey(key))
                {
                    if (m_CustomShadersBusy.Contains(key))
                        return;

                    ResourceId freed = m_CustomShaders[key];
                    m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                    {
                        r.FreeCustomShader(freed);
                    });

                    m_CustomShaders.Remove(key);

                    var text = customShader.Text;

                    for (int i = 0; i < customShader.Items.Count; i++)
                    {
                        if (customShader.Items[i].ToString() == fn)
                        {
                            customShader.Items.RemoveAt(i);
                            break;
                        }
                    }

                    customShader.Text = text;
                }
            }

            foreach (var f in Directory.EnumerateFiles(Core.ConfigDirectory, "*.hlsl"))
            {
                var fn = Path.GetFileNameWithoutExtension(f);
                var key = fn.ToLowerInvariant();

                if (!m_CustomShaders.ContainsKey(key))
                {
                    string source = File.ReadAllText(f);

                    m_CustomShaders.Add(key, ResourceId.Null);
                    m_CustomShadersBusy.Add(key);
                    m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                    {
                        string errors = "";

                        ResourceId id = r.BuildCustomShader("main", source, 0, ShaderStageType.Pixel, out errors);

                        if (m_CustomShaderEditor.ContainsKey(key))
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                m_CustomShaderEditor[key].ShowErrors(errors);
                            });
                        }

                        BeginInvoke((MethodInvoker)delegate
                        {
                            customShader.Items.Add(fn);
                            m_CustomShaders[key] = id;
                            m_CustomShadersBusy.Remove(key);

                            customShader.AutoCompleteSource = AutoCompleteSource.None;
                            customShader.AutoCompleteSource = AutoCompleteSource.ListItems;

                            UI_UpdateChannels();
                        });
                    });
                }
            }
        }

        private void customCreate_Click(object sender, EventArgs e)
        {
            if (customShader.Text == null || customShader.Text == "")
            {
                MessageBox.Show("No name entered.\nEnter a name in the textbox.", "Error Creating Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (m_CustomShaders.ContainsKey(customShader.Text.ToLowerInvariant()))
            {
                MessageBox.Show("Selected shader already exists.\nEnter a new name in the textbox.", "Error Creating Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var path = Path.Combine(Core.ConfigDirectory, customShader.Text + ".hlsl");
            File.WriteAllText(path, "float4 main(float4 pos : SV_Position, float4 uv : TEXCOORD0) : SV_Target0\n{\n\treturn float4(0,0,0,1);\n}\n");

            // auto-open edit window
            customEdit_Click(sender, e);
        }

        private void customEdit_Click(object sender, EventArgs e)
        {
            var filename = customShader.Text;

            var files = new Dictionary<string, string>();
            files.Add(filename, File.ReadAllText(Path.Combine(Core.ConfigDirectory, filename + ".hlsl")));
            ShaderViewer s = new ShaderViewer(m_Core, true, "Custom Shader", files,

            // Save Callback
            (ShaderViewer viewer, Dictionary<string, string> updatedfiles) =>
            {
                foreach (var f in updatedfiles)
                {
                    var path = Path.Combine(Core.ConfigDirectory, f.Key + ".hlsl");
                    File.WriteAllText(path, f.Value);
                }
            },

            // Close Callback
            () =>
            {
                m_CustomShaderEditor.Remove(filename);
            });

            m_CustomShaderEditor[customShader.Text] = s;

            s.Show(this.DockPanel);
        }

        private void customDelete_Click(object sender, EventArgs e)
        {
            if (customShader.Text == null || customShader.Text == "")
            {
                MessageBox.Show("No shader selected.\nSelect a custom shader from the drop-down", "Error Deleting Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!m_CustomShaders.ContainsKey(customShader.Text.ToLowerInvariant()))
            {
                MessageBox.Show("Selected shader doesn't exist.\nSelect a custom shader from the drop-down", "Error Deleting Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult res = MessageBox.Show(String.Format("Really delete {0}?", customShader.Text), "Deleting Custom Shader", MessageBoxButtons.YesNoCancel);

            if (res == DialogResult.Yes)
            {
                var path = Path.Combine(Core.ConfigDirectory, customShader.Text + ".hlsl");
                if(!File.Exists(path))
                {
                    MessageBox.Show(String.Format("Shader file {0} can't be found.\nSelect a custom shader from the drop-down", customShader.Text),
                                    "Error Deleting Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    MessageBox.Show(String.Format("Error deleting shader {0}.\nSelect a custom shader from the drop-down", customShader.Text),
                                    "Error Deleting Shader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                customShader.Text = "";
                UI_UpdateChannels();
            }
        }

        #endregion

        #region ILogViewerForm

        public void OnLogfileLoaded()
        {
            var outConfig = new OutputConfig();
            outConfig.m_Type = OutputType.TexDisplay;

            saveTex.Enabled = true;

            m_Following = new Following(FollowType.RT_UAV, 0);

            IntPtr contextHandle = pixelContext.Handle;
            IntPtr renderHandle = render.Handle;
            m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
            {
                m_Output = r.CreateOutput(renderHandle);
                m_Output.SetPixelContext(contextHandle);
                m_Output.SetOutputConfig(outConfig);

                this.BeginInvoke(new Action(UI_CreateThumbnails));
            });

            m_FSWatcher = new FileSystemWatcher(Core.ConfigDirectory, "*.hlsl");
            m_FSWatcher.EnableRaisingEvents = true;
            m_FSWatcher.Changed += new FileSystemEventHandler(CustomShaderModified);
            m_FSWatcher.Renamed += new RenamedEventHandler(CustomShaderModified);
            m_FSWatcher.Created += new FileSystemEventHandler(CustomShaderModified);
            m_FSWatcher.Deleted += new FileSystemEventHandler(CustomShaderModified);
            ReloadCustomShaders("");

            texturefilter.SelectedIndex = 0;
            texturefilter.Text = "";
            textureList.FillTextureList("", true, true);

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
        }

        void CustomShaderModified(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(5);
            BeginInvoke((MethodInvoker)delegate
            {
                ReloadCustomShaders(e.Name);
            });
        }

        public void OnLogfileClosed()
        {
            if (IsDisposed) return;

            if(m_FSWatcher != null)
                m_FSWatcher.EnableRaisingEvents = false;
            m_FSWatcher = null;

            m_Output = null;

            saveTex.Enabled = false;

            rtPanel.ClearThumbnails();
            texPanel.ClearThumbnails();

            texturefilter.SelectedIndex = 0;

            m_TexDisplay = new TextureDisplay();

            PixelPicked = false;

            statusLabel.Text = "";
            m_PreviewPanel.Text = "Current";
            zoomOption.Text = "";
            mipLevel.Items.Clear();
            sliceFace.Items.Clear();
            rangeHistogram.SetRange(0.0f, 1.0f);

            checkerBack.Checked = true;
            backcolorPick.Checked = false;

            checkerBack.Enabled = backcolorPick.Enabled = false;

            channels.SelectedIndex = 0;
            overlay.SelectedIndex = 0;

            customShader.Items.Clear();
            m_CustomShaders.Clear();

            textureList.Items.Clear();

            render.Invalidate();

            renderHScroll.Enabled = false;
            renderVScroll.Enabled = false;

            hoverSwatch.BackColor = Color.Black;

            var tabs = m_PreviewPanel.Pane.TabStripControl.Tabs;

            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Content != m_PreviewPanel)
                {
                    (tabs[i].Content as DockContent).Close();
                    i--;
                }
            }

            (m_PreviewPanel as DockContent).Show();

            UI_UpdateTextureDetails();
            UI_UpdateChannels();
        }

        public void OnEventSelected(UInt32 frameID, UInt32 eventID)
        {
            if (IsDisposed) return;

            UI_OnTextureSelectionChanged();

            ResourceId[] RTs = m_Core.CurPipelineState.GetOutputTargets();
            ResourceId Depth = m_Core.CurPipelineState.OutputDepth;
            ResourceId[] Texs = m_Core.CurPipelineState.GetResources(ShaderStageType.Pixel);

            ShaderReflection details = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Pixel);

            uint firstuav = uint.MaxValue;

            if (m_Core.APIProps.pipelineType == APIPipelineStateType.D3D11 &&
                m_Core.CurD3D11PipelineState != null)
                firstuav = m_Core.CurD3D11PipelineState.m_OM.UAVStartSlot;

            if (m_Output == null) return;

            int i = 0;
            foreach (var prev in rtPanel.Thumbnails)
            {
                if (prev.SlotName == "D" && Depth != ResourceId.Null)
                {
                    FetchTexture tex = null;
                    foreach (var t in m_Core.CurTextures)
                        if (t.ID == Depth)
                            tex = t;

                    FetchBuffer buf = null;
                    foreach (var b in m_Core.CurBuffers)
                        if (b.ID == Depth)
                            buf = b;

                    if (tex != null)
                    {
                        prev.Init(tex.name, tex.width, tex.height, tex.depth, tex.mips);
                        IntPtr handle = prev.ThumbnailHandle;
                        m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                        {
                            m_Output.AddThumbnail(handle, DisableThumbnails ? ResourceId.Null : Depth);
                        });
                    }
                    else if (buf != null)
                    {
                        prev.Init(buf.name, buf.length, 0, 0, Math.Max(1, buf.structureSize));
                        IntPtr handle = prev.ThumbnailHandle;
                        m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                        {
                            m_Output.AddThumbnail(handle, ResourceId.Null);
                        });
                    }
                    else
                    {
                        prev.Init();
                    }

                    prev.Tag = new Following(FollowType.Depth, 0);
                    prev.Visible = true;
                }
                else if (i < RTs.Length && RTs[i] != ResourceId.Null)
                {
                    FetchTexture tex = null;
                    foreach (var t in m_Core.CurTextures)
                        if (t.ID == RTs[i])
                            tex = t;

                    FetchBuffer buf = null;
                    foreach (var b in m_Core.CurBuffers)
                        if (b.ID == RTs[i])
                            buf = b;

                    string bindName = "";

                    if (details != null && i >= firstuav)
                    {
                        foreach (var bind in details.Resources)
                        {
                            if (bind.bindPoint == i && bind.IsUAV)
                            {
                                bindName = "<" + bind.name + ">";
                            }
                        }
                    }

                    if (tex != null)
                    {
                        prev.Init(!tex.customName && bindName != "" ? bindName : tex.name, tex.width, tex.height, tex.depth, tex.mips);
                        IntPtr handle = prev.ThumbnailHandle;
                        ResourceId id = RTs[i];
                        m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                        {
                            m_Output.AddThumbnail(handle, DisableThumbnails ? ResourceId.Null : id);
                        });
                    }
                    else if (buf != null)
                    {
                        prev.Init(!buf.customName && bindName != "" ? bindName : buf.name, buf.length, 0, 0, Math.Max(1, buf.structureSize));
                        IntPtr handle = prev.ThumbnailHandle;
                        m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                        {
                            m_Output.AddThumbnail(handle, ResourceId.Null);
                        });
                    }
                    else
                    {
                        prev.Init();
                    }

                    prev.Tag = new Following(FollowType.RT_UAV, i);

                    if (i >= firstuav)
                        prev.SlotName = "U" + i;
                    else
                        prev.SlotName = i.ToString();

                    prev.Visible = true;
                }
                else if (prev.Selected)
                {
                    prev.Init();
                    IntPtr handle = prev.ThumbnailHandle;
                    m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                    {
                        m_Output.AddThumbnail(handle, ResourceId.Null);
                    });
                }
                else
                {
                    prev.Init();
                    prev.Visible = false;
                }

                i++;
            }

            rtPanel.RefreshLayout();

            i = 0;
            foreach (var prev in texPanel.Thumbnails)
            {
                if (i >= Texs.Length)
                    break;

                bool used = false;

                string bindName = "";

                if (details != null)
                {
                    foreach (var bind in details.Resources)
                    {
                        if (bind.bindPoint == i && bind.IsSRV)
                        {
                            used = true;
                            bindName = "<" + bind.name + ">";
                        }
                    }
                }

                // show if
                if (used || // it's referenced by the shader - regardless of empty or not
                    (showDisabled.Checked && !used && Texs[i] != ResourceId.Null) || // it's bound, but not referenced, and we have "show disabled"
                    (showEmpty.Checked && Texs[i] == ResourceId.Null) // it's empty, and we have "show empty"
                    )
                {
                    FetchTexture tex = null;
                    foreach (var t in m_Core.CurTextures)
                        if (t.ID == Texs[i])
                            tex = t;

                    if (tex != null)
                    {
                        prev.Init(!tex.customName && bindName != "" ? bindName : tex.name, tex.width, tex.height, tex.depth, tex.mips);
                        IntPtr handle = prev.ThumbnailHandle;
                        ResourceId id = Texs[i];
                        m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                        {
                            m_Output.AddThumbnail(handle, DisableThumbnails ? ResourceId.Null : id);
                        });
                    }
                    else
                    {
                        prev.Init();
                    }

                    prev.Tag = new Following(FollowType.PSResource, i);
                    prev.Visible = true;
                }
                else if (prev.Selected)
                {
                    FetchTexture tex = null;
                    foreach (var t in m_Core.CurTextures)
                        if (t.ID == Texs[i])
                            tex = t;

                    IntPtr handle = prev.ThumbnailHandle;
                    if (Texs[i] == ResourceId.Null || tex == null)
                        prev.Init();
                    else
                        prev.Init("Unused", tex.width, tex.height, tex.depth, tex.mips);
                    m_Core.Renderer.BeginInvoke((ReplayRenderer rep) =>
                    {
                        m_Output.AddThumbnail(handle, ResourceId.Null);
                    });
                }
                else
                {
                    prev.Init();
                    prev.Visible = false;
                }

                i++;
            }

            texPanel.RefreshLayout();

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);

            if(autoFit.Checked)
                AutoFitRange();
        }

        #endregion

        #region Update UI state

        private void UI_CreateThumbnails()
        {
            rtPanel.SuspendLayout();
            texPanel.SuspendLayout();

            for (int i = 0; i < 8; i++)
            {
                var prev = new ResourcePreview(m_Core, m_Output);
                prev.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
                prev.SlotName = i.ToString();
                prev.MouseClick += thumbsLayout_MouseClick;
                prev.MouseDoubleClick += thumbsLayout_MouseDoubleClick;
                rtPanel.AddThumbnail(prev);

                if(i == 0)
                    prev.Selected = true;
            }

            {
                var prev = new ResourcePreview(m_Core, m_Output);
                prev.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
                prev.SlotName = "D";
                prev.MouseClick += thumbsLayout_MouseClick;
                prev.MouseDoubleClick += thumbsLayout_MouseDoubleClick;
                rtPanel.AddThumbnail(prev);
            }

            for (int i = 0; i < 128; i++)
            {
                var prev = new ResourcePreview(m_Core, m_Output);
                prev.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
                prev.SlotName = i.ToString();
                prev.MouseClick += thumbsLayout_MouseClick;
                prev.MouseDoubleClick += thumbsLayout_MouseDoubleClick;
                texPanel.AddThumbnail(prev);
            }

            foreach (var c in rtPanel.Thumbnails)
                c.Visible = false;

            foreach (var c in texPanel.Thumbnails)
                c.Visible = false;

            rtPanel.ResumeLayout();
            texPanel.ResumeLayout();
        }

        private void UI_OnTextureSelectionChanged()
        {
            FetchTexture tex = CurrentTexture;

            if (tex == null) return;

            if (m_TexDisplay.texid != tex.ID &&
                m_Core.Config.TextureViewer_ResetRange)
            {
                rangeHistogram.RangeMin = 0.0f;
                rangeHistogram.RangeMax = 1.0f;
                rangeHistogram.BlackPoint = 0.0f;
                rangeHistogram.WhitePoint = 1.0f;
            }

            m_TexDisplay.texid = tex.ID;

            m_CurPixelValue = null;
            m_CurRealValue = null;

            ScrollPosition = ScrollPosition;

            UI_UpdateStatusText();

            mipLevel.Items.Clear();
            sliceFace.Items.Clear();

            for (int i = 0; i < tex.mips; i++)
                mipLevel.Items.Add(i + " - " + Math.Max(1, tex.width >> i) + "x" + Math.Max(1, tex.height >> i));

            mipLevel.SelectedIndex = 0;
            m_TexDisplay.mip = 0;
            m_TexDisplay.sliceFace = 0;

            if (tex.mips == 1)
            {
                mipLevel.Enabled = false;
            }
            else
            {
                mipLevel.Enabled = true;
            }

            if (tex.numSubresources == tex.mips && tex.depth <= 1)
            {
                sliceFace.Enabled = false;
            }
            else
            {
                sliceFace.Enabled = true;

                sliceFace.Visible = sliceFaceLabel.Visible = true;

                String[] cubeFaces = { "X+", "X-", "Y+", "Y-", "Z+", "Z-" };

                UInt32 numSlices = (Math.Max(1, tex.depth) * tex.numSubresources) / tex.mips;

                for (UInt32 i = 0; i < numSlices; i++)
                {
                    if (tex.cubemap)
                    {
                        String name = cubeFaces[i%6];
                        if (numSlices > 6)
                            name = string.Format("[{0}] {1}", (i / 6), cubeFaces[i%6]); // Front 1, Back 2, 3, 4 etc for cube arrays
                        sliceFace.Items.Add(name);
                    }
                    else
                    {
                        sliceFace.Items.Add("Slice " + i);
                    }
                }

                sliceFace.SelectedIndex = (int)m_Following.GetFirstArraySlice(m_Core);
            }

            UI_UpdateFittedScale();

            //render.Width = (int)(CurrentTexDisplayWidth * m_TexDisplay.scale);
            //render.Height = (int)(CurrentTexDisplayHeight * m_TexDisplay.scale);

            UI_UpdateTextureDetails();

            UI_UpdateChannels();

            m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
            {
                RT_UpdateVisualRange(r);

                RT_UpdateAndDisplay(r);

                if (tex.ID != ResourceId.Null)
                {
                    var us = r.GetUsage(tex.ID);

                    var tb = m_Core.TimelineBar;

                    if (tb != null && tb.Visible && !tb.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            tb.HighlightResource(tex.ID, tex.name, us);
                        }));
                    }
                }
            });
        }

        void dockPanel_ActiveDocumentChanged(object sender, EventArgs e)
        {
            var d = dockPanel.ActiveDocument as DockContent;

            if (d == null) return;

            if (d.Visible)
                d.Controls.Add(renderToolstripContainer);

            UI_OnTextureSelectionChanged();
        }

        void PreviewPanel_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((sender as Control).Visible == false && renderToolstripContainer.Parent != sender)
                return;

            var tabs = m_PreviewPanel.Pane.TabStripControl.Tabs;

            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Content == sender)
                {
                    var dc = m_PreviewPanel;

                    if (i > 0)
                    {
                        dc = (tabs[i - 1].Content as DockContent);
                    }
                    else if (i < tabs.Count - 1)
                    {
                        dc = (tabs[i + 1].Content as DockContent);
                    }

                    dc.Controls.Add(renderToolstripContainer);
                    dc.Show();

                    return;
                }
            }

            m_PreviewPanel.Controls.Add(renderToolstripContainer);
            m_PreviewPanel.Show();
        }

        private void UI_UpdateTextureDetails()
        {
            texStatusDim.Text = "";

            if (m_Core.CurTextures == null || CurrentTexture == null)
            {
                m_PreviewPanel.Text = "Unbound";

                texStatusDim.Text = "";
                return;
            }

            FetchTexture current = CurrentTexture;

            ResourceId followID = m_Following.GetResourceId(m_Core);

            {
                FetchTexture tex = null;
                foreach (var t in m_Core.CurTextures)
                    if (t.ID == followID)
                        tex = t;

                if (tex != null)
                {
                    switch (m_Following.Type)
                    {
                        case FollowType.RT_UAV:
                            m_PreviewPanel.Text = string.Format("Cur OM Target {0} - {1}", m_Following.index, tex.name);
                            break;
                        case FollowType.Depth:
                            m_PreviewPanel.Text = string.Format("Cur DSV - {0}", tex.name);
                            break;
                        case FollowType.PSResource:
                            m_PreviewPanel.Text = string.Format("Cur PS SRV {0} - {1}", m_Following.index, tex.name);
                            break;
                    }
                }
                else
                {
                    m_PreviewPanel.Text = "Current";

                    if (followID == ResourceId.Null)
                        m_PreviewPanel.Text = "Unbound";
                }
            }

            texStatusDim.Text = current.name + " - ";
            
            if (current.dimension >= 1)
                texStatusDim.Text += current.width;
            if (current.dimension >= 2)
                texStatusDim.Text += "x" + current.height;
            if (current.dimension >= 3)
                texStatusDim.Text += "x" + current.depth;

            if (current.arraysize > 1)
                texStatusDim.Text += "[" + current.arraysize + "]";

            if(current.msQual > 0 || current.msSamp > 1)
                texStatusDim.Text += string.Format(" MS{{{0}x {1}Q}}", current.msSamp, current.msQual);

            texStatusDim.Text += " " + current.mips + " mips";

            texStatusDim.Text += " - " + current.format.ToString();
        }

        private bool PixelPicked
        {
            get
            {
                return (m_CurPixelValue != null);
            }

            set
            {
                if (value == true)
                {
                    debugPixel.Enabled = debugPixelContext.Enabled = true;
                    debugPixel.Text = debugPixelContext.Text = "Debug this Pixel";
                }
                else
                {
                    m_CurPixelValue = null;
                    m_CurRealValue = null;

                    debugPixel.Enabled = debugPixelContext.Enabled = false;
                    debugPixel.Text = debugPixelContext.Text = "RMB to Pick";
                }

                pixelContext.Invalidate();
            }
        }

        private void UI_UpdateStatusText()
        {
            if (textureList.InvokeRequired)
            {
                this.BeginInvoke(new Action(UI_UpdateStatusText));
                return;
            }

            FetchTexture tex = CurrentTexture;

            if (tex == null) return;

            bool dsv = ((tex.creationFlags & TextureCreationFlags.DSV) != 0);
            bool uintTex = (tex.format.compType == FormatComponentType.UInt);
            bool sintTex = (tex.format.compType == FormatComponentType.SInt);

            if (m_CurHoverValue != null)
            {
                if (dsv || uintTex || sintTex)
                {
                    hoverSwatch.BackColor = Color.Black;
                }
                else
                {
                    float r = Helpers.Clamp(m_CurHoverValue.value.f[0], 0.0f, 1.0f);
                    float g = Helpers.Clamp(m_CurHoverValue.value.f[1], 0.0f, 1.0f);
                    float b = Helpers.Clamp(m_CurHoverValue.value.f[2], 0.0f, 1.0f);

                    if (tex.format.srgbCorrected || (tex.creationFlags & TextureCreationFlags.SwapBuffer) > 0)
                    {
                        r = (float)Math.Pow(r, 1.0f / 2.2f);
                        g = (float)Math.Pow(g, 1.0f / 2.2f);
                        b = (float)Math.Pow(b, 1.0f / 2.2f);
                    }

                    hoverSwatch.BackColor = Color.FromArgb((int)(255.0f * r), (int)(255.0f * g), (int)(255.0f * b));
                }
            }

            string statusText = "Hover - " + (m_CurHoverPixel.X >> (int)m_TexDisplay.mip) + ", " + (m_CurHoverPixel.Y >> (int)m_TexDisplay.mip);

            if (m_CurPixelValue != null)
            {
                statusText += " - Right click - " +
                                (m_PickedPoint.X >> (int)m_TexDisplay.mip) + "," + (m_PickedPoint.Y >> (int)m_TexDisplay.mip) + ": ";

                PixelValue val = m_CurPixelValue;

                if (m_TexDisplay.CustomShader != ResourceId.Null && m_CurRealValue != null)
                {
                    statusText += Formatter.Format(val.value.f[0]) + ", " +
                                  Formatter.Format(val.value.f[1]) + ", " +
                                  Formatter.Format(val.value.f[2]) + ", " +
                                  Formatter.Format(val.value.f[3]);

                    val = m_CurRealValue;
                    
                    statusText += " (Real: ";
                }

                if (dsv)
                {
                    statusText += "Depth ";
                    if (uintTex)
                    {
                        if(tex.format.compByteWidth == 2)
                            statusText += Formatter.Format(val.value.u16[0]);
                        else              
                            statusText += Formatter.Format(val.value.u[0]);
                    }
                    else
                    {
                        statusText += Formatter.Format(val.value.f[0]);
                    }
                    statusText += String.Format(", Stencil {0} / 0x{0:X2}", (int)(255.0f * val.value.f[1]));
                }
                else
                {
                    if (uintTex)
                    {
                        statusText += val.value.u[0].ToString() + ", " +
                                      val.value.u[1].ToString() + ", " +
                                      val.value.u[2].ToString() + ", " +
                                      val.value.u[3].ToString();
                    }
                    else if (sintTex)
                    {
                        statusText += val.value.i[0].ToString() + ", " +
                                      val.value.i[1].ToString() + ", " +
                                      val.value.i[2].ToString() + ", " +
                                      val.value.i[3].ToString();
                    }
                    else
                    {
                        statusText += Formatter.Format(val.value.f[0]) + ", " +
                                      Formatter.Format(val.value.f[1]) + ", " +
                                      Formatter.Format(val.value.f[2]) + ", " +
                                      Formatter.Format(val.value.f[3]);
                    }
                }

                if (m_TexDisplay.CustomShader != ResourceId.Null)
                    statusText += ")";

                PixelPicked = true;
            }
            else
            {
                statusText += " - Right click to pick a pixel";

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (m_Output != null)
                        m_Output.DisablePixelContext();
                });

                PixelPicked = false;
            }

            statusLabel.Text = statusText;
        }

        private void UI_UpdateChannels()
        {
            FetchTexture tex = CurrentTexture;

            channelStrip.SuspendLayout();

            if (tex != null && (tex.creationFlags & TextureCreationFlags.DSV) > 0 &&
                (string)channels.SelectedItem != "Custom")
            {
                customRed.Visible = false;
                customGreen.Visible = false;
                customBlue.Visible = false;
                customAlpha.Visible = false;
                mulLabel.Visible = false;
                hdrMul.Visible = false;
                customShader.Visible = false;
                customCreate.Visible = false;
                customEdit.Visible = false;
                customDelete.Visible = false;
                depthStencilToolstrip.Visible = true;

                backcolorPick.Visible = false;
                checkerBack.Visible = false;

                mulSep.Visible = false;

                m_TexDisplay.Red = depthDisplay.Checked;
                m_TexDisplay.Green = stencilDisplay.Checked;
                m_TexDisplay.Blue = false;
                m_TexDisplay.Alpha = false;

                m_TexDisplay.HDRMul = -1.0f;
                if (m_TexDisplay.CustomShader != ResourceId.Null) { m_CurPixelValue = null; m_CurRealValue = null; UI_UpdateStatusText(); }
                m_TexDisplay.CustomShader = ResourceId.Null;
            }
            else if ((string)channels.SelectedItem == "RGBA" || !m_Core.LogLoaded)
            {
                customRed.Visible = true;
                customGreen.Visible = true;
                customBlue.Visible = true;
                customAlpha.Visible = true;
                mulLabel.Visible = false;
                hdrMul.Visible = false;
                customShader.Visible = false;
                customCreate.Visible = false;
                customEdit.Visible = false;
                customDelete.Visible = false;
                depthStencilToolstrip.Visible = false;

                backcolorPick.Visible = true;
                checkerBack.Visible = true;

                checkerBack.Enabled = backcolorPick.Enabled = customAlpha.Checked;

                mulSep.Visible = false;

                m_TexDisplay.Red = customRed.Checked;
                m_TexDisplay.Green = customGreen.Checked;
                m_TexDisplay.Blue = customBlue.Checked;
                m_TexDisplay.Alpha = customAlpha.Checked;

                m_TexDisplay.HDRMul = -1.0f;
                if (m_TexDisplay.CustomShader != ResourceId.Null) { m_CurPixelValue = null; m_CurRealValue = null; UI_UpdateStatusText(); }
                m_TexDisplay.CustomShader = ResourceId.Null;
            }
            else if ((string)channels.SelectedItem == "RGBM")
            {
                customRed.Visible = true;
                customGreen.Visible = true;
                customBlue.Visible = true;
                customAlpha.Visible = false;
                mulLabel.Visible = true;
                hdrMul.Visible = true;
                customShader.Visible = false;
                customCreate.Visible = false;
                customEdit.Visible = false;
                customDelete.Visible = false;
                depthStencilToolstrip.Visible = false;

                backcolorPick.Visible = false;
                checkerBack.Visible = false;

                mulSep.Visible = true;

                m_TexDisplay.Red = customRed.Checked;
                m_TexDisplay.Green = customGreen.Checked;
                m_TexDisplay.Blue = customBlue.Checked;
                m_TexDisplay.Alpha = false;

                float mul = 32.0f;

                if (!float.TryParse(hdrMul.Text, out mul))
                    hdrMul.Text = mul.ToString();

                m_TexDisplay.HDRMul = mul;
                if (m_TexDisplay.CustomShader != ResourceId.Null) { m_CurPixelValue = null; m_CurRealValue = null; UI_UpdateStatusText(); }
                m_TexDisplay.CustomShader = ResourceId.Null;
            }
            else if ((string)channels.SelectedItem == "Custom")
            {
                customRed.Visible = false;
                customGreen.Visible = false;
                customBlue.Visible = false;
                customAlpha.Visible = false;
                mulLabel.Visible = false;
                hdrMul.Visible = false;
                customShader.Visible = true;
                customCreate.Visible = true;
                customEdit.Visible = true;
                customDelete.Visible = true;
                depthStencilToolstrip.Visible = false;

                backcolorPick.Visible = false;
                checkerBack.Visible = false;

                mulSep.Visible = false;

                m_TexDisplay.Red = customRed.Checked;
                m_TexDisplay.Green = customGreen.Checked;
                m_TexDisplay.Blue = customBlue.Checked;
                m_TexDisplay.Alpha = customAlpha.Checked;

                m_TexDisplay.HDRMul = -1.0f;

                m_TexDisplay.CustomShader = ResourceId.Null;
                if (m_CustomShaders.ContainsKey(customShader.Text.ToLowerInvariant()))
                {
                    if (m_TexDisplay.CustomShader == ResourceId.Null) { m_CurPixelValue = null; m_CurRealValue = null; UI_UpdateStatusText(); }
                    m_TexDisplay.CustomShader = m_CustomShaders[customShader.Text.ToLowerInvariant()];
                    customDelete.Enabled = customEdit.Enabled = true;
                    customCreate.Enabled = false;
                }
                else
                {
                    customDelete.Enabled = customEdit.Enabled = false;
                    customCreate.Enabled = true;
                }
            }

            channelStrip.ResumeLayout();

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
        }

        private void RT_UpdateAndDisplay(ReplayRenderer r)
        {
            if (m_Output == null) return;

            m_Output.SetTextureDisplay(m_TexDisplay);

            render.Invalidate();
        }

        private void pixelContext_Paint(object sender, PaintEventArgs e)
        {
            if (m_Output == null || m_Core.Renderer == null || PixelPicked == false)
            {
                e.Graphics.Clear(Color.Black);
                return;
            }

            m_Core.Renderer.Invoke((ReplayRenderer r) => { if (m_Output != null) m_Output.Display(); });
        }

        private void render_Paint(object sender, PaintEventArgs e)
        {
            renderContainer.Invalidate();
            if (m_Output == null || m_Core.Renderer == null)
            {
                e.Graphics.Clear(Color.Black);
                return;
            }

            foreach (var prev in rtPanel.Thumbnails)
                if (prev.Unbound) prev.Clear();

            foreach (var prev in texPanel.Thumbnails)
                if (prev.Unbound) prev.Clear();

            m_Core.Renderer.Invoke((ReplayRenderer r) => { if (m_Output != null) m_Output.Display(); });
        }

        #endregion

        #region Scale Handling

        private FetchTexture FollowingTexture
        {
            get
            {
                if (!m_Core.LogLoaded || m_Core.CurTextures == null) return null;

                ResourceId ID = m_Following.GetResourceId(m_Core);

                if (ID == ResourceId.Null)
                    ID = m_TexDisplay.texid;

                for (int i = 0; i < m_Core.CurTextures.Length; i++)
                {
                    if (m_Core.CurTextures[i].ID == ID)
                    {
                        return m_Core.CurTextures[i];
                    }
                }

                return null;
            }
        }
        private FetchTexture CurrentTexture
        {
            get
            {
                var dc = renderToolstripContainer.Parent as DockContent;

                if (dc != null && dc.Tag != null)
                    return dc.Tag as FetchTexture;

                return FollowingTexture;
            }
        }

        private UInt32 CurrentTexDisplayWidth
        {
            get
            {
                if (CurrentTexture == null)
                    return 1;

                return CurrentTexture.width;
            }
        }
        private UInt32 CurrentTexDisplayHeight
        {
            get
            {
                if (CurrentTexture == null)
                    return 1;

                if (CurrentTexture.dimension == 1)
                    return 100;

                return CurrentTexture.height;
            }
        }

        private bool FitToWindow
        {
            get
            {
                return fitToWindow.Checked;
            }

            set
            {
                if (!FitToWindow && value)
                {
                    fitToWindow.Checked = true;
                }
                else if (FitToWindow && !value)
                {
                    fitToWindow.Checked = false;
                    float curScale = m_TexDisplay.scale;
                    zoomOption.SelectedText = "";
                    CurrentZoomValue = curScale;
                }
            }
        }

        private float GetFitScale()
        {
            float xscale = (float)render.Width / (float)CurrentTexDisplayWidth;
            float yscale = (float)render.Height / (float)CurrentTexDisplayHeight;
            return Math.Min(xscale, yscale);
        }

        private void UI_UpdateFittedScale()
        {
            if (FitToWindow)
                UI_SetScale(1.0f);
        }

        private void UI_SetScale(float s)
        {
            UI_SetScale(s, render.ClientRectangle.Width / 2, render.ClientRectangle.Height / 2);
        }

        bool ScrollUpdateScrollbars = true;

        Point ScrollPosition
        {
            get
            {
                return new Point((int)m_TexDisplay.offx, (int)m_TexDisplay.offy);
            }

            set
            {
                m_TexDisplay.offx = Math.Max(render.Width - CurrentTexDisplayWidth * m_TexDisplay.scale, value.X);
                m_TexDisplay.offy = Math.Max(render.Height - CurrentTexDisplayHeight * m_TexDisplay.scale, value.Y);

                m_TexDisplay.offx = Math.Min(0.0f, (float)m_TexDisplay.offx);
                m_TexDisplay.offy = Math.Min(0.0f, (float)m_TexDisplay.offy);

                if (ScrollUpdateScrollbars)
                {
                    if(renderHScroll.Enabled)
                        renderHScroll.Value = (int)Math.Min(renderHScroll.Maximum, (int)-m_TexDisplay.offx);

                    if (renderVScroll.Enabled)
                        renderVScroll.Value = (int)Math.Min(renderVScroll.Maximum, (int)-m_TexDisplay.offy);
                }

                m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
            }
        }

        private void UI_SetScale(float s, int x, int y)
        {
            if (FitToWindow)
                s = GetFitScale();

            float prevScale = m_TexDisplay.scale;

            m_TexDisplay.scale = Math.Max(0.1f, Math.Min(8.0f, s));

            FetchTexture tex = CurrentTexture;

            if (tex == null)
            {
                if(m_Core.LogLoaded)
                    foreach (var t in m_Core.CurTextures)
                        if (t.ID == m_TexDisplay.texid)
                            tex = t;

                if(tex == null)
                    return;
            }

            //render.Width = Math.Min(500, (int)(tex.width * m_TexDisplay.scale));
            //render.Height = Math.Min(500, (int)(tex.height * m_TexDisplay.scale));

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);

            float scaleDelta = (m_TexDisplay.scale / prevScale);

            Point newPos = ScrollPosition;

            newPos -= new Size(x, y);
            newPos = new Point((int)(newPos.X * scaleDelta), (int)(newPos.Y * scaleDelta));
            newPos += new Size(x, y);

            ScrollPosition = newPos;

            CurrentZoomValue = m_TexDisplay.scale;

            CalcScrollbars();
        }

        private void CalcScrollbars()
        {
            if (Math.Floor(CurrentTexDisplayWidth * m_TexDisplay.scale) <= render.Width)
            {
                renderHScroll.Enabled = false;
            }
            else
            {
                renderHScroll.Enabled = true;

                renderHScroll.Maximum = (int)Math.Ceiling(CurrentTexDisplayWidth * m_TexDisplay.scale - (float)render.Width);
                renderHScroll.LargeChange = Math.Max(1, renderHScroll.Maximum/6);
            }

            if (Math.Floor(CurrentTexDisplayHeight * m_TexDisplay.scale) <= render.Height)
            {
                renderVScroll.Enabled = false;
            }
            else
            {
                renderVScroll.Enabled = true;

                renderVScroll.Maximum = (int)Math.Ceiling(CurrentTexDisplayHeight * m_TexDisplay.scale - (float)render.Height);
                renderVScroll.LargeChange = Math.Max(1, renderVScroll.Maximum / 6);
            }
        }

        private void render_Layout(object sender, LayoutEventArgs e)
        {
            UI_UpdateFittedScale();
            CalcScrollbars();

            renderContainer.Invalidate();
        }

        #endregion

        #region Mouse movement and scrolling

        private Point m_DragStartScroll;
        private Point m_DragStartPos;

        private Point m_CurHoverPixel;
        private Point m_PickedPoint;

        private PixelValue m_CurRealValue = null;
        private PixelValue m_CurPixelValue = null;
        private PixelValue m_CurHoverValue = null;

        private void RT_UpdateHoverColour(PixelValue v)
        {
            m_CurHoverValue = v;

            this.BeginInvoke(new Action(UI_UpdateStatusText));
        }

        private void RT_PickPixelsAndUpdate(int x, int y, bool ctx)
        {
            if(ctx)
                m_Output.SetPixelContextLocation((UInt32)x, (UInt32)y);

            var pickValue = m_Output.PickPixel(m_TexDisplay.texid, true, (UInt32)x, (UInt32)y,
                                                    m_TexDisplay.sliceFace, m_TexDisplay.mip);
            PixelValue realValue = null;
            if (m_TexDisplay.CustomShader != ResourceId.Null)
                realValue = m_Output.PickPixel(m_TexDisplay.texid, false, (UInt32)x, (UInt32)y,
                                                    m_TexDisplay.sliceFace, m_TexDisplay.mip);

            RT_UpdatePixelColour(pickValue, realValue, false);
        }

        private void RT_UpdatePixelColour(PixelValue withCustom, PixelValue realValue, bool UpdateHover)
        {
            m_CurPixelValue = withCustom;
            if (UpdateHover)
                m_CurHoverValue = withCustom;
            m_CurRealValue = realValue;

            this.BeginInvoke(new Action(UI_UpdateStatusText));
        }

        private void render_KeyDown(object sender, KeyEventArgs e)
        {
            bool nudged = false;

            if (e.KeyCode == Keys.Up)
            {
                m_PickedPoint = new Point(m_PickedPoint.X, m_PickedPoint.Y - 1);
                nudged = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                m_PickedPoint = new Point(m_PickedPoint.X, m_PickedPoint.Y + 1);
                nudged = true;
            }
            else if (e.KeyCode == Keys.Left)
            {
                m_PickedPoint = new Point(m_PickedPoint.X - 1, m_PickedPoint.Y);
                nudged = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                m_PickedPoint = new Point(m_PickedPoint.X + 1, m_PickedPoint.Y);
                nudged = true;
            }

            if(nudged)
            {
                e.Handled = true;

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (m_Output != null)
                        RT_PickPixelsAndUpdate(m_PickedPoint.X, m_PickedPoint.Y, true);

                    RT_UpdateAndDisplay(r);
                });

                UI_UpdateStatusText();
            }
        }

        private void renderHScroll_Scroll(object sender, ScrollEventArgs e)
        {
            ScrollUpdateScrollbars = false;

            if (e.Type != ScrollEventType.EndScroll)
                ScrollPosition = new Point(ScrollPosition.X - (e.NewValue - e.OldValue), ScrollPosition.Y);

            ScrollUpdateScrollbars = true;
        }

        private void renderVScroll_Scroll(object sender, ScrollEventArgs e)
        {
            ScrollUpdateScrollbars = false;

            if(e.Type != ScrollEventType.EndScroll)
                ScrollPosition = new Point(ScrollPosition.X, ScrollPosition.Y - (e.NewValue - e.OldValue));

            ScrollUpdateScrollbars = true;
        }

        private void render_MouseLeave(object sender, EventArgs e)
        {
            Cursor = Cursors.Default;
        }

        private void render_MouseUp(object sender, MouseEventArgs e)
        {
            Cursor = Cursors.Default;
        }

        private void render_MouseMove(object sender, MouseEventArgs e)
        {
            m_CurHoverPixel = render.PointToClient(Cursor.Position);

            m_CurHoverPixel.X = (int)(((float)m_CurHoverPixel.X - m_TexDisplay.offx) / m_TexDisplay.scale);
            m_CurHoverPixel.Y = (int)(((float)m_CurHoverPixel.Y - m_TexDisplay.offy) / m_TexDisplay.scale);

            if (e.Button == MouseButtons.Right && m_TexDisplay.texid != ResourceId.Null)
            {
                m_PickedPoint = m_CurHoverPixel;

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (m_Output != null)
                        RT_PickPixelsAndUpdate(m_CurHoverPixel.X, m_CurHoverPixel.Y, true);
                });

                Cursor = Cursors.Cross;
            }

            if (e.Button == MouseButtons.None && m_TexDisplay.texid != ResourceId.Null)
            {
                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (m_Output != null)
                        RT_UpdateHoverColour(m_Output.PickPixel(m_TexDisplay.texid, true, (UInt32)m_CurHoverPixel.X, (UInt32)m_CurHoverPixel.Y,
                                                                  m_TexDisplay.sliceFace, m_TexDisplay.mip));
                });

            }

            Panel p = renderContainer;

            Point curpos = Cursor.Position;

            if (e.Button == MouseButtons.Left)
            {
                if (Math.Abs(m_DragStartPos.X - curpos.X) > p.HorizontalScroll.SmallChange ||
                    Math.Abs(m_DragStartPos.Y - curpos.Y) > p.VerticalScroll.SmallChange)
                {
                    ScrollPosition = new Point(m_DragStartScroll.X + (curpos.X - m_DragStartPos.X),
                                               m_DragStartScroll.Y + (curpos.Y - m_DragStartPos.Y));
                }

                Cursor = Cursors.NoMove2D;
            }

            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
            {
                Cursor = Cursors.Default;
            }

            UI_UpdateStatusText();
        }

        private void render_MouseClick(object sender, MouseEventArgs e)
        {
            render.Focus();

            if (e.Button == MouseButtons.Right)
            {
                m_PickedPoint = m_CurHoverPixel;

                m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
                {
                    if (m_Output != null)
                        RT_PickPixelsAndUpdate(m_CurHoverPixel.X, m_CurHoverPixel.Y, true);
                });

                Cursor = Cursors.Cross;
            }

            if (e.Button == MouseButtons.Left)
            {
                m_DragStartPos = Cursor.Position;
                m_DragStartScroll = ScrollPosition;

                Cursor = Cursors.NoMove2D;
            }
        }

        private void render_MouseWheel(object sender, MouseEventArgs e)
        {
            Point cursorPos = renderContainer.PointToClient(Cursor.Position);

            FitToWindow = false;

            // scroll in logarithmic scale
            double logScale = Math.Log(m_TexDisplay.scale);
            logScale += e.Delta / 2500.0;
            UI_SetScale((float)Math.Exp(logScale), cursorPos.X, cursorPos.Y);

            ((HandledMouseEventArgs)e).Handled = true;
        }

        #endregion

        #region Texture Display Options

        private float CurrentZoomValue
        {
            get
            {
                if (FitToWindow)
                    return m_TexDisplay.scale;

                int zoom = 100;
                Int32.TryParse(zoomOption.Text.ToString().Replace('%', ' '), out zoom);
                return (float)(zoom) / 100.0f;
            }

            set
            {
                if(!zoomOption.IsDisposed)
                    zoomOption.Text = (Math.Ceiling(value * 100)).ToString() + "%";
            }
        }

        private void zoomOption_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\n' || e.KeyChar == '\r')
            {
                string txt = zoomOption.Text;
                FitToWindow = false;
                zoomOption.Text = txt;

                UI_SetScale(CurrentZoomValue);
            }
        }

        private void zoomOption_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                string txt = zoomOption.Text;
                FitToWindow = false;
                zoomOption.Text = txt;

                UI_SetScale(CurrentZoomValue);
            }
        }

        private void zoomOption_SelectedIndexChanged(object sender, EventArgs e)
        {
            if ((zoomOption.Focused || zoomOption.ContentRectangle.Contains(Cursor.Position))
                && zoomOption.SelectedItem != null)
            {
                var item = zoomOption.SelectedItem.ToString();

                FitToWindow = false;

                zoomOption.Text = item;

                UI_SetScale(CurrentZoomValue);
            }
        }

        private void zoomOption_DropDownClosed(object sender, EventArgs e)
        {
            if (zoomOption.SelectedItem != null)
            {
                var item = zoomOption.SelectedItem.ToString();

                FitToWindow = false;

                zoomOption.Text = item;

                UI_SetScale(CurrentZoomValue);
            }
        }


        private void fitToWindow_CheckedChanged(object sender, EventArgs e)
        {
            UI_UpdateFittedScale();
        }

        private void backcolorPick_Click(object sender, EventArgs e)
        {
            var result = colorDialog.ShowDialog();

            if (result == DialogResult.OK || result == DialogResult.Yes)
            {
                m_TexDisplay.darkBackgroundColour =
                    m_TexDisplay.lightBackgroundColour = new FloatVector(
                        ((float)colorDialog.Color.R) / 255.0f,
                        ((float)colorDialog.Color.G) / 255.0f,
                        ((float)colorDialog.Color.B) / 255.0f);

                backcolorPick.Checked = true;
                checkerBack.Checked = false;
            }

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
        }

        private void checkerBack_Click(object sender, EventArgs e)
        {
            var defaults = new TextureDisplay();

            m_TexDisplay.darkBackgroundColour = defaults.darkBackgroundColour;
            m_TexDisplay.lightBackgroundColour = defaults.lightBackgroundColour;

            backcolorPick.Checked = false;
            checkerBack.Checked = true;

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
        }
        private void mipLevel_SelectedIndexChanged(object sender, EventArgs e)
        {
            m_TexDisplay.mip = (UInt32)mipLevel.SelectedIndex;

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
        }

        private void overlay_SelectedIndexChanged(object sender, EventArgs e)
        {
            m_TexDisplay.overlay = TextureDisplayOverlay.None;

            if (overlay.SelectedIndex > 0)
                m_TexDisplay.overlay = (TextureDisplayOverlay)overlay.SelectedIndex;

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
        }

        private void sliceFace_SelectedIndexChanged(object sender, EventArgs e)
        {
            m_TexDisplay.sliceFace = (UInt32)sliceFace.SelectedIndex;

            m_Core.Renderer.BeginInvoke(RT_UpdateAndDisplay);
            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
        }

        private void updateChannelsHandler(object sender, EventArgs e)
        {
            UI_UpdateChannels();
        }

        private void channelButton_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && sender is ToolStripButton)
            {
                ToolStripButton me = (sender as ToolStripButton);

                bool checkd = false;

                var butts = new ToolStripButton[] { customRed, customGreen, customBlue, customAlpha };

                foreach (var b in butts)
                {
                    if(b.Checked && b != sender)
                        checkd = true;
                    if(!b.Checked && b == sender)
                        checkd = true;
                }

                customRed.Checked = !checkd;
                customGreen.Checked = !checkd;
                customBlue.Checked = !checkd;
                customAlpha.Checked = !checkd;
                (sender as ToolStripButton).Checked = checkd;
            }
        }

        Thread rangePaintThread = null;

        void rangeHistogram_RangeUpdated(object sender, Controls.RangeHistogramEventArgs e)
        {
            m_TexDisplay.rangemin = e.BlackPoint;
            m_TexDisplay.rangemax = e.WhitePoint;

            rangeBlack.Text = Formatter.Format(e.BlackPoint);
            rangeWhite.Text = Formatter.Format(e.WhitePoint);

            if (rangePaintThread != null &&
                rangePaintThread.ThreadState != ThreadState.Aborted &&
                rangePaintThread.ThreadState != ThreadState.Stopped)
            {
                return;
            }

            rangePaintThread = new Thread(new ThreadStart(() =>
            {
                m_Core.Renderer.Invoke((ReplayRenderer r) => { RT_UpdateAndDisplay(r); if (m_Output != null) m_Output.Display(); });
                Thread.Sleep(8);
            }));
            rangePaintThread.Start();
        }

        #endregion

        #region Handlers

        private void zoomRange_Click(object sender, EventArgs e)
        {
            float black = rangeHistogram.BlackPoint;
            float white = rangeHistogram.WhitePoint;

            autoFit.Checked = false;

            rangeHistogram.RangeMin = black;
            rangeHistogram.RangeMax = white;

            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
        }

        private void reset01_Click(object sender, EventArgs e)
        {
            rangeHistogram.SetRange(0.0f, 1.0f);

            autoFit.Checked = false;

            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
        }

        private void autoFit_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                autoFit.Checked = !autoFit.Checked;

                if (autoFit.Checked)
                    AutoFitRange();
            }
        }

        private void autoFit_Click(object sender, EventArgs e)
        {
            AutoFitRange();
        }

        private void AutoFitRange()
        {
            m_Core.Renderer.BeginInvoke((ReplayRenderer r) =>
            {
                PixelValue min, max;
                bool success = r.GetMinMax(m_TexDisplay.texid, m_TexDisplay.sliceFace, m_TexDisplay.mip, out min, out max);

                if (success)
                {
                    float minval = float.MaxValue;
                    float maxval = -float.MaxValue;

                    bool changeRange = false;

                    ResourceFormat fmt = CurrentTexture.format;

                    for (int i = 0; i < 4; i++)
                    {
                        if (fmt.compType == FormatComponentType.UInt)
                        {
                            min.value.f[i] = min.value.u[i];
                            max.value.f[i] = max.value.u[i];
                        }
                        else if (fmt.compType == FormatComponentType.SInt)
                        {
                            min.value.f[i] = min.value.i[i];
                            max.value.f[i] = max.value.i[i];
                        }
                    }

                    if (m_TexDisplay.Red)
                    {
                        minval = Math.Min(minval, min.value.f[0]);
                        maxval = Math.Max(maxval, max.value.f[0]);
                        changeRange = true;
                    }
                    if (m_TexDisplay.Green && fmt.compCount > 1)
                    {
                        minval = Math.Min(minval, min.value.f[1]);
                        maxval = Math.Max(maxval, max.value.f[1]);
                        changeRange = true;
                    }
                    if (m_TexDisplay.Blue && fmt.compCount > 2)
                    {
                        minval = Math.Min(minval, min.value.f[2]);
                        maxval = Math.Max(maxval, max.value.f[2]);
                        changeRange = true;
                    }
                    if (m_TexDisplay.Alpha && fmt.compCount > 3)
                    {
                        minval = Math.Min(minval, min.value.f[3]);
                        maxval = Math.Max(maxval, max.value.f[3]);
                        changeRange = true;
                    }

                    if (changeRange)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            rangeHistogram.SetRange(minval, maxval);
                            m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
                        }));
                    }
                }
            });
        }

        private bool m_Visualise = false;

        private void visualiseRange_CheckedChanged(object sender, EventArgs e)
        {
            if (visualiseRange.Checked)
            {
                rangeHistogram.MinimumSize = new Size(300, 90);

                m_Visualise = true;
                m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
            }
            else
            {
                m_Visualise = false;
                rangeHistogram.MinimumSize = new Size(200, 20);

                rangeHistogram.HistogramData = null;
            }
        }

        private void RT_UpdateVisualRange(ReplayRenderer r)
        {
            if (!m_Visualise || CurrentTexture == null) return;

            ResourceFormat fmt = CurrentTexture.format;

            bool success = true;

            uint[] histogram;
            success = r.GetHistogram(m_TexDisplay.texid, m_TexDisplay.sliceFace, m_TexDisplay.mip,
                                     rangeHistogram.BlackPoint, rangeHistogram.WhitePoint,
                                     m_TexDisplay.Red,
                                     m_TexDisplay.Green && fmt.compCount > 1,
                                     m_TexDisplay.Blue && fmt.compCount > 2,
                                     m_TexDisplay.Alpha && fmt.compCount > 3,
                                     out histogram);

            if (success)
            {
                this.BeginInvoke(new Action(() =>
                {
                    rangeHistogram.SetHistogramRange(rangeHistogram.BlackPoint, rangeHistogram.WhitePoint);
                    rangeHistogram.HistogramData = histogram;
                }));
            }
        }

        private void rangePoint_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                float black = rangeHistogram.BlackPoint;
                float white = rangeHistogram.WhitePoint;

                float.TryParse(rangeBlack.Text, out black);
                float.TryParse(rangeWhite.Text, out white);

                rangeHistogram.SetRange(black, white);

                m_Core.Renderer.BeginInvoke(RT_UpdateVisualRange);
            }
        }

        private void tabContextMenu_Opening(object sender, CancelEventArgs e)
        {
            if (tabContextMenu.SourceControl == m_PreviewPanel.Pane.TabStripControl)
            {
                int idx = m_PreviewPanel.Pane.TabStripControl.Tabs.IndexOf(m_PreviewPanel.Pane.ActiveContent);

                if (idx == -1)
                    e.Cancel = true;

                if (m_PreviewPanel.Pane.ActiveContent == m_PreviewPanel)
                    closeTab.Enabled = false;
                else
                    closeTab.Enabled = true;
            }
        }

        private void closeTab_Click(object sender, EventArgs e)
        {
            if (tabContextMenu.SourceControl == m_PreviewPanel.Pane.TabStripControl)
            {
                int idx = m_PreviewPanel.Pane.TabStripControl.Tabs.IndexOf(m_PreviewPanel.Pane.ActiveContent);

                if (m_PreviewPanel.Pane.ActiveContent != m_PreviewPanel)
                {
                    (m_PreviewPanel.Pane.ActiveContent as DockContent).Close();
                }
            }
        }

        private void closeOtherTabs_Click(object sender, EventArgs e)
        {
            if (tabContextMenu.SourceControl == m_PreviewPanel.Pane.TabStripControl)
            {
                IDockContent active = m_PreviewPanel.Pane.ActiveContent;

                var tabs = m_PreviewPanel.Pane.TabStripControl.Tabs;

                for(int i=0; i < tabs.Count; i++)
                {
                    if (tabs[i].Content != active && tabs[i].Content != m_PreviewPanel)
                    {
                        (tabs[i].Content as DockContent).Close();
                        i--;
                    }
                }

                (active as DockContent).Show();
            }
        }

        private void closeTabsToRight_Click(object sender, EventArgs e)
        {
            if (tabContextMenu.SourceControl == m_PreviewPanel.Pane.TabStripControl)
            {
                int idx = m_PreviewPanel.Pane.TabStripControl.Tabs.IndexOf(m_PreviewPanel.Pane.ActiveContent);

                var tabs = m_PreviewPanel.Pane.TabStripControl.Tabs;

                while (tabs.Count > idx+1)
                {
                    (m_PreviewPanel.Pane.TabStripControl.Tabs[idx + 1].Content as DockContent).Close();
                }
            }
        }

        private void TextureViewer_Resize(object sender, EventArgs e)
        {
            render.Invalidate();
        }

        private void debugPixel_Click(object sender, EventArgs e)
        {
            ShaderDebugTrace trace = null;

            ShaderReflection shaderDetails = m_Core.CurPipelineState.GetShaderReflection(ShaderStageType.Pixel);

            m_Core.Renderer.Invoke((ReplayRenderer r) =>
            {
                trace = r.PSGetDebugStates((UInt32)m_PickedPoint.X, (UInt32)m_PickedPoint.Y);
            });

            if (trace == null || trace.states.Length == 0)
            {
                MessageBox.Show("Couldn't find pixel to debug.\nEnsure the relevant drawcall is selected for this pixel.", "No Pixel Found",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            this.BeginInvoke(new Action(() =>
            {
                ShaderViewer s = new ShaderViewer(m_Core, shaderDetails, ShaderStageType.Pixel, trace);

                s.Show(this.DockPanel);
            }));
        }

        private void saveTex_Click(object sender, EventArgs e)
        {
            if (saveTextureDialog.ShowDialog() == DialogResult.OK)
            {
                bool ret = false;

                m_Core.Renderer.Invoke((ReplayRenderer r) =>
                {
                    ret = r.SaveTexture(m_TexDisplay.texid, m_TexDisplay.mip, saveTextureDialog.FileName);
                });

                if(!ret)
                    MessageBox.Show(string.Format("Error saving texture {0}.\n\nCheck diagnostic log in Help menu for more details.", saveTextureDialog.FileName),
                                       "Error saving texture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void texturefilter_TextChanged(object sender, EventArgs e)
        {
            textureList.FillTextureList(texturefilter.SelectedIndex <= 0 ? texturefilter.Text : "",
                                        texturefilter.SelectedIndex == 1,
                                        texturefilter.SelectedIndex == 2);
        }

        private void texturefilter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                texturefilter.SelectedIndex = 0;
                texturefilter.Text = "";
            }
        }

        private void clearTexFilter_Click(object sender, EventArgs e)
        {
            texturefilter.SelectedIndex = 0;
            texturefilter.Text = "";
        }

        private void toolstripEnabledChanged(object sender, EventArgs e)
        {
            overlayStrip.Visible = overlayStripEnabled.Checked;
            overlayStrip.Visible = overlayStripEnabled.Checked;
            channelStrip.Visible = channelsStripEnabled.Checked;
            zoomStrip.Visible = zoomStripEnabled.Checked;
            rangeStrip.Visible = rangeStripEnabled.Checked;
        }

        private void mainToolstrips_TopToolStripPanel_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                toolstripMenu.Show((Control)sender, e.Location);
        }

        private void texListShow_Click(object sender, EventArgs e)
        {
            if (!m_TexlistDockPanel.Visible)
            {
                texturefilter.SelectedIndex = 0;
                texturefilter.Text = "";

                m_TexlistDockPanel.Show();
            }
            else
            {
                m_TexlistDockPanel.Hide();
            }
        }

        void textureList_GoIconClick(object sender, GoIconClickEventArgs e)
        {
            ViewTexture(e.ID, false);
        }

        #endregion

        #region Thumbnail strip

        private void AddResourceUsageEntry(uint start, uint end, ResourceUsage usage)
        {
            ToolStripItem item = null;

            if (start == end)
                item = rightclickMenu.Items.Add("EID " + start + ": " + usage.Str());
            else
                item = rightclickMenu.Items.Add("EID " + start + "-" + end + ": " + usage.Str());

            item.Click += new EventHandler(resourceContextItem_Click);
            item.Tag = end;
        }

        private void OpenResourceContextMenu(ResourceId id, bool thumbStripMenu, Control c, Point p)
        {
            int i = 0;
            for (i = 0; i < rightclickMenu.Items.Count; i++)
                if (rightclickMenu.Items[i] == usedStartLabel)
                    break;

            while (i != rightclickMenu.Items.Count - 1)
                rightclickMenu.Items.RemoveAt(i + 1);

            for (i = 0; i < rightclickMenu.Items.Count; i++)
            {
                if (rightclickMenu.Items[i] == usedStartLabel)
                    break;

                rightclickMenu.Items[i].Visible = thumbStripMenu;
            }

            if (id != ResourceId.Null)
            {
                usedSep.Visible = true;
                usedStartLabel.Visible = true;
                openNewTab.Visible = true;

                openNewTab.Tag = id;

                m_Core.Renderer.Invoke((ReplayRenderer r) =>
                {
                    EventUsage[] usage = r.GetUsage(id);

                    this.BeginInvoke(new Action(() =>
                    {
                        uint start = 0;
                        uint end = 0;
                        ResourceUsage us = ResourceUsage.IA_IB;

                        foreach (var u in usage)
                        {
                            if (start == 0)
                            {
                                start = end = u.eventID;
                                us = u.usage;
                                continue;
                            }

                            var curDraw = m_Core.GetDrawcall(m_Core.CurFrame, u.eventID);

                            if (u.usage != us || curDraw.previous == null || curDraw.previous.eventID != end)
                            {
                                AddResourceUsageEntry(start, end, us);
                                start = end = u.eventID;
                                us = u.usage;
                            }

                            end = u.eventID;
                        }

                        if (start != 0)
                            AddResourceUsageEntry(start, end, us);

                        rightclickMenu.Show(c, p);
                    }));
                });
            }
            else
            {
                usedSep.Visible = false;
                usedStartLabel.Visible = false;
                openNewTab.Visible = false;

                rightclickMenu.Show(c, p);
            }
        }

        private void thumbsLayout_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && sender is ResourcePreview)
            {
                var prev = (ResourcePreview)sender;

                var follow = (Following)prev.Tag;

                var id = m_Following.GetResourceId(m_Core);

                if (id != ResourceId.Null)
                    ViewTexture(id, false);
            }
        }

        private void thumbsLayout_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && sender is ResourcePreview)
            {
                var prev = (ResourcePreview)sender;

                var follow = (Following)prev.Tag;

                foreach (var p in rtPanel.Thumbnails)
                    p.Selected = false;

                foreach (var p in texPanel.Thumbnails)
                    p.Selected = false;

                m_Following = follow;
                prev.Selected = true;

                var id = m_Following.GetResourceId(m_Core);

                if (id != ResourceId.Null)
                {
                    UI_OnTextureSelectionChanged();
                    m_PreviewPanel.Show();
                }
            }

            if (e.Button == MouseButtons.Right)
            {
                ResourceId id = ResourceId.Null;

                if (sender is ResourcePreview)
                {
                    var prev = (ResourcePreview)sender;

                    var tagdata = (Following)prev.Tag;

                    id = tagdata.GetResourceId(m_Core);

                    if (id == ResourceId.Null && tagdata == m_Following)
                        id = m_TexDisplay.texid;
                }

                OpenResourceContextMenu(id, true, (Control)sender, e.Location);
            }
        }

        private void textureList_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                OpenResourceContextMenu(FollowingTexture == null ? ResourceId.Null : FollowingTexture.ID, false,
                                            (Control)sender, e.Location);
        }

        void resourceContextItem_Click(object sender, EventArgs e)
        {
            if (sender is ToolStripItem)
            {
                var c = (ToolStripItem)sender;

                if (c.Tag is uint)
                    m_Core.SetEventID(null, m_Core.CurFrame, (uint)c.Tag);
                else if (c.Tag is ResourceId)
                    ViewTexture((ResourceId)c.Tag, false);
            }
        }

        private void showDisabled_Click(object sender, EventArgs e)
        {
            showDisabled.Checked = !showDisabled.Checked;

            if (m_Core.LogLoaded)
                OnEventSelected(m_Core.CurFrame, m_Core.CurEvent);
        }

        private void showEmpty_Click(object sender, EventArgs e)
        {
            showEmpty.Checked = !showEmpty.Checked;

            if (m_Core.LogLoaded)
                OnEventSelected(m_Core.CurFrame, m_Core.CurEvent);
        }

        #endregion

        private void TextureViewer_FormClosed(object sender, FormClosedEventArgs e)
        {
            m_Core.RemoveLogViewer(this);
        }
   }
}