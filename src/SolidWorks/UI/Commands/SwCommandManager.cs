﻿//*********************************************************************
//xCAD
//Copyright(C) 2021 Xarial Pty Limited
//Product URL: https://www.xcad.net
//License: https://xcad.xarial.com/license/
//*********************************************************************

using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xarial.XCad.Base;
using Xarial.XCad.Base.Enums;
using Xarial.XCad.SolidWorks.Exceptions;
using Xarial.XCad.SolidWorks.Services;
using Xarial.XCad.SolidWorks.UI.Commands.Exceptions;
using Xarial.XCad.SolidWorks.UI.Commands.Toolkit.Enums;
using Xarial.XCad.SolidWorks.UI.Commands.Toolkit.Structures;
using Xarial.XCad.SolidWorks.Utils;
using Xarial.XCad.UI;
using Xarial.XCad.UI.Commands;
using Xarial.XCad.UI.Commands.Enums;
using Xarial.XCad.UI.Commands.Structures;
using Xarial.XCad.Utils.Diagnostics;
using Xarial.XCad.Toolkit;

namespace Xarial.XCad.SolidWorks.UI.Commands
{
    public interface ISwCommandManager : IXCommandManager, IDisposable
    {
        ICommandManager CmdMgr { get; }
    }

    /// <inheritdoc/>
    internal class SwCommandManager : ISwCommandManager
    {
        private class CommandInfo
        {
            internal SwCommandGroup Grp { get; }
            internal CommandSpec Spec { get; }

            internal CommandInfo(SwCommandGroup grp, CommandSpec spec)
            {
                Grp = grp;
                Spec = spec;
            }
        }

        private class TabCommandInfo
        {
            internal int CmdId { get; private set; }
            internal swDocumentTypes_e DocType { get; private set; }
            internal swCommandTabButtonTextDisplay_e TextType { get; private set; }

            internal TabCommandInfo(swDocumentTypes_e docType, int cmdId,
                swCommandTabButtonTextDisplay_e textType)
            {
                DocType = docType;
                CmdId = cmdId;
                TextType = textType;
            }
        }

        private const string SUB_GROUP_SEPARATOR = "\\";

        private readonly ISwApplication m_App;

        private readonly List<SwCommandGroup> m_CommandBars;

        private readonly Dictionary<string, CommandInfo> m_Commands;

        private readonly IXLogger m_Logger;

        /// <summary>
        /// Pointer to command group which holding the add-in commands
        /// </summary>
        public ICommandManager CmdMgr { get; private set; }

        public IXCommandGroup[] CommandGroups => m_CommandBars.ToArray();

        private readonly IServiceProvider m_SvcProvider;
        private readonly Guid m_AddInGuid;

        internal SwCommandManager(ISwApplication app, int addinCookie, IServiceProvider svcProvider, Guid addInGuid)
        {
            m_App = app;
            m_AddInGuid = addInGuid;

            CmdMgr = m_App.Sw.GetCommandManager(addinCookie);

            m_SvcProvider = svcProvider;

            m_Logger = svcProvider.GetService<IXLogger>();
            m_Commands = new Dictionary<string, CommandInfo>();
            m_CommandBars = new List<SwCommandGroup>();
        }

        public IXCommandGroup AddCommandGroup(CommandGroupSpec cmdBar)
        {
            return AddCommandGroupOrContextMenu(cmdBar, false, null);
        }

        public IXCommandGroup AddContextMenu(CommandGroupSpec cmdBar, SelectType_e? owner)
        {
            swSelectType_e? selType = null;
            
            if (owner.HasValue) 
            {
                selType = (swSelectType_e)owner;
            }

            return AddCommandGroupOrContextMenu(cmdBar, true, selType);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        internal SwCommandGroup AddCommandGroupOrContextMenu(CommandGroupSpec cmdBar,
            bool isContextMenu, swSelectType_e? contextMenuSelectType)
        {
            m_Logger.Log($"Creating command group: {cmdBar.Id}", LoggerMessageSeverity_e.Debug);

            if (m_CommandBars.FirstOrDefault(b => b.Spec.Id == cmdBar.Id) != null)
            {
                throw new GroupIdAlreadyExistsException(cmdBar);
            }

            var title = GetMenuPath(cmdBar);

            var cmdGroup = CreateCommandGroup(cmdBar.Id, title, cmdBar.Tooltip,
                cmdBar.Commands.Select(c => c.UserId).ToArray(), isContextMenu,
                contextMenuSelectType);
            
            var bar = new SwCommandGroup(m_App, cmdBar, cmdGroup);

            m_CommandBars.Add(bar);

            using (var iconsConv = m_SvcProvider.GetService<IIconsCreator>())
            {
                CreateIcons(cmdGroup, cmdBar, iconsConv);

                var createdCmds = CreateCommandItems(bar, cmdBar.Id, cmdBar.Commands);

                var tabGroup = GetRootCommandGroup(cmdBar);

                try
                {
                    CreateCommandTabBox(tabGroup, createdCmds);
                }
                catch (Exception ex)
                {
                    m_Logger.Log(ex);
                    //not critical error - continue operation
                }
            }

            return bar;
        }

        internal void HandleCommandClick(string cmdId)
        {
            m_Logger.Log($"Command clicked: {cmdId}", XCad.Base.Enums.LoggerMessageSeverity_e.Debug);

            CommandInfo cmd;

            if (m_Commands.TryGetValue(cmdId, out cmd))
            {
                cmd.Grp.RaiseCommandClick(cmd.Spec);
            }
            else
            {
                Debug.Assert(false, "All callbacks must be registered");
            }
        }

        internal int HandleCommandEnable(string cmdId)
        {
            CommandInfo cmd;

            if (m_Commands.TryGetValue(cmdId, out cmd))
            {
                return (int)cmd.Grp.RaiseCommandEnable(cmd.Spec);
            }
            else
            {
                Debug.Assert(false, "All callbacks must be registered");
            }

            return (int)CommandItemEnableState_e.DeselectDisable;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                var usedToolbarIds = new List<int>();

                foreach (var grp in m_CommandBars)
                {
                    if (grp.CommandGroup.HasToolbar)
                    {
                        usedToolbarIds.Add(grp.CommandGroup.ToolbarId);
                    }

                    m_Logger.Log($"Removing group: {grp.Spec.Id}", LoggerMessageSeverity_e.Debug);

                    bool removeRes;

                    if (m_App.IsVersionNewerOrEqual(Enums.SwVersion_e.Sw2011))
                    {
                        var res = (swRemoveCommandGroupErrors)CmdMgr.RemoveCommandGroup2(grp.Spec.Id, true);
                        removeRes = res == swRemoveCommandGroupErrors.swRemoveCommandGroup_Success;
                    }
                    else
                    {
                        removeRes = CmdMgr.RemoveCommandGroup(grp.Spec.Id);
                    }

                    if (!removeRes)
                    {
                        m_Logger.Log($"Failed to remove group: {grp.Spec.Id}", LoggerMessageSeverity_e.Warning);
                    }
                }

                TryClearDanglingToolbarIds(usedToolbarIds);

                m_CommandBars.Clear();
            }

            if (CmdMgr != null)
            {
                if (Marshal.IsComObject(CmdMgr))
                {
                    Marshal.ReleaseComObject(CmdMgr);
                }

                CmdMgr = null;
            }
        }

        //NOTE: this is a workaround method as ICommandManager::RemoveCommandGroup2 seems to ignore the RuntimeOnly flag and always keep the group in the registry
        //furthermore the IgnorePreviousVersion parameter of ICommandManager::CreateCommandGroup2 seems to only work after the restart
        //this results in the cached toolbar id loaded for the dangling group if its user id reused
        private void TryClearDanglingToolbarIds(IEnumerable<int> usedGroupIds) 
        {
            try
            {
                var rev = m_App.Sw.RevisionNumber().Split('.');
                var majorRev = int.Parse(rev[0]);
                const int REV_VERS_OFFSET = 2005 - 13; //SW 2005 corresponds to revions 13 and each major version revision is incremented by 1

                var swVers = majorRev + REV_VERS_OFFSET;

                var customApiToolbarsRegKeyName = $@"Software\Solidworks\SOLIDWORKS {swVers}\User Interface\Custom API Toolbars";
                
                var customApiToolbarsRegKey = Registry.CurrentUser.OpenSubKey(customApiToolbarsRegKeyName, true);

                if (customApiToolbarsRegKey != null)
                {
                    var toolbarIds = customApiToolbarsRegKey.GetSubKeyNames();

                    foreach (var toolbarId in toolbarIds)
                    {
                        if (!usedGroupIds.Contains(int.Parse(toolbarId)))
                        {
                            var toolbarKey = customApiToolbarsRegKey.OpenSubKey(toolbarId, false);
                            var moduleGuid = Guid.Parse((string)toolbarKey.GetValue("ModuleName"));

                            if (moduleGuid.Equals(m_AddInGuid))
                            {
                                m_Logger.Log($"Clearing the registry key '{toolbarId}' at 'HKEY_CURRENT_USER\\{customApiToolbarsRegKeyName}'", LoggerMessageSeverity_e.Debug);
                                customApiToolbarsRegKey.DeleteSubKey(toolbarId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_Logger.Log(ex);
            }
        }

        private void ClearCommandTabBox(ICommandTabBox cmdBox)
        {
            m_Logger.Log($"Clearing Command Tab Box", LoggerMessageSeverity_e.Debug);

            object existingCmds;
            
            cmdBox.GetCommands(out existingCmds, out _);

            if (existingCmds != null)
            {
                cmdBox.RemoveCommands(existingCmds as int[]);
            }
        }

        private bool CompareIDs(IEnumerable<int> storedIDs, IEnumerable<int> addinIDs)
            => storedIDs.OrderBy(x => x).SequenceEqual(addinIDs.OrderBy(x => x));

        private swCommandTabButtonTextDisplay_e ConvertTextDisplay(RibbonTabTextDisplay_e style)
        {
            switch (style)
            {
                case RibbonTabTextDisplay_e.NoText:
                    return swCommandTabButtonTextDisplay_e.swCommandTabButton_NoText;

                case RibbonTabTextDisplay_e.TextBelow:
                    return swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;

                case RibbonTabTextDisplay_e.TextHorizontal:
                    return swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;

                default:
                    return swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;
            }
        }

        private CommandGroup CreateCommandGroup(int groupId, string title, string toolTip,
            int[] knownCmdIDs, bool isContextMenu, swSelectType_e? contextMenuSelectType)
        {
            int cmdGroupErr = 0;

            object registryIDs;

            var isChanged = false;

            if (CmdMgr.GetGroupDataFromRegistry(groupId, out registryIDs))
            {
                m_Logger.Log("Commands cached in the registry", LoggerMessageSeverity_e.Debug);

                isChanged = !CompareIDs(registryIDs as int[], knownCmdIDs);
            }

            m_Logger.Log($"Command ids changed: {isChanged}", LoggerMessageSeverity_e.Debug);

            CommandGroup cmdGroup;

            if (isContextMenu)
            {
                cmdGroup = CmdMgr.AddContextMenu(groupId, title);
                if (contextMenuSelectType.HasValue)
                {
                    cmdGroup.SelectType = (int)contextMenuSelectType;
                }
            }
            else
            {
                cmdGroup = CmdMgr.CreateCommandGroup2(groupId, title, toolTip,
                    toolTip, -1, isChanged, ref cmdGroupErr);

                m_Logger.Log($"Command group creation result: {(swCreateCommandGroupErrors)cmdGroupErr}", LoggerMessageSeverity_e.Debug);

                Debug.Assert(cmdGroupErr == (int)swCreateCommandGroupErrors.swCreateCommandGroup_Success);
            }

            return cmdGroup;
        }

        private Dictionary<CommandSpec, int> CreateCommandItems(SwCommandGroup cmdGroup, int groupId, CommandSpec[] cmds)
        {
            var dupIds = cmds.Where(c => c.UserId > 0).GroupBy(c => c.UserId).Where(g => g.Count() > 1).ToArray();

            if (dupIds.Any()) 
            {
                throw new DuplicateCommandUserIdsException(cmdGroup.Spec.Title, groupId, dupIds.Select(x => x.Key).ToArray());
            }

            var createdCmds = new Dictionary<CommandSpec, int>();

            var callbackMethodName = nameof(SwAddInEx.OnCommandClick);
            var enableMethodName = nameof(SwAddInEx.OnCommandEnable);

            for (int i = 0; i < cmds.Length; i++)
            {
                var cmd = cmds[i];

                swCommandItemType_e menuToolbarOpts = 0;

                if (cmd.HasMenu)
                {
                    menuToolbarOpts |= swCommandItemType_e.swMenuItem;
                }

                if (cmd.HasToolbar)
                {
                    menuToolbarOpts |= swCommandItemType_e.swToolbarItem;
                }

                if (menuToolbarOpts == 0)
                {
                    throw new InvalidMenuToolbarOptionsException(cmd);
                }

                var cmdName = $"{groupId}.{cmd.UserId}";

                m_Commands.Add(cmdName, new CommandInfo(cmdGroup, cmd));

                var callbackFunc = $"{callbackMethodName}({cmdName})";
                var enableFunc = $"{enableMethodName}({cmdName})";

                if (cmd.HasSpacer)
                {
                    cmdGroup.CommandGroup.AddSpacer2(-1, (int)menuToolbarOpts);
                }

                var cmdIndex = cmdGroup.CommandGroup.AddCommandItem2(cmd.Title, -1, cmd.Tooltip,
                    cmd.Title, i, callbackFunc, enableFunc, cmd.UserId,
                    (int)menuToolbarOpts);

                createdCmds.Add(cmd, cmdIndex);

                m_Logger.Log($"Created command {cmd.Title}:{cmdIndex} for {cmd.UserId}", LoggerMessageSeverity_e.Debug);
            }

            cmdGroup.CommandGroup.HasToolbar = cmds.Any(c => c.HasToolbar);
            cmdGroup.CommandGroup.HasMenu = cmds.Any(c => c.HasMenu);

            if (!cmdGroup.CommandGroup.Activate()) 
            {
                m_Logger.Log("Command group activation failed", LoggerMessageSeverity_e.Error);
            }

            m_Logger.Log($"Command group-{groupId} Id: {(cmdGroup.CommandGroup.HasToolbar ? cmdGroup.CommandGroup.ToolbarId.ToString() : "No Toolbar")}", LoggerMessageSeverity_e.Debug);

            return createdCmds.ToDictionary(p => p.Key, p =>
            {
                var cmdId = cmdGroup.CommandGroup.CommandID[p.Value];
                m_Logger.Log($"Command-{p.Value} Id: {cmdId}", LoggerMessageSeverity_e.Debug);
                return cmdId;
            });
        }

        private void CreateCommandTabBox(CommandGroup cmdGroup, Dictionary<CommandSpec, int> commands)
        {
            m_Logger.Log($"Creating command tab box", LoggerMessageSeverity_e.Debug);

            var tabCommands = new List<TabCommandInfo>();

            foreach (var cmdData in commands)
            {
                var cmd = cmdData.Key;
                var cmdId = cmdData.Value;

                if (cmd.HasTabBox)
                {
                    var docTypes = new List<swDocumentTypes_e>();

                    if (cmd.SupportedWorkspace.HasFlag(WorkspaceTypes_e.Part))
                    {
                        docTypes.Add(swDocumentTypes_e.swDocPART);
                    }

                    if (cmd.SupportedWorkspace.HasFlag(WorkspaceTypes_e.Assembly))
                    {
                        docTypes.Add(swDocumentTypes_e.swDocASSEMBLY);
                    }

                    if (cmd.SupportedWorkspace.HasFlag(WorkspaceTypes_e.Drawing))
                    {
                        docTypes.Add(swDocumentTypes_e.swDocDRAWING);
                    }

                    tabCommands.AddRange(docTypes.Select(
                        t => new TabCommandInfo(
                            t, cmdId, ConvertTextDisplay(cmd.TabBoxStyle))));
                }
            }

            foreach (var cmdGrp in tabCommands.GroupBy(c => c.DocType))
            {
                var docType = cmdGrp.Key;

                var cmdTab = CmdMgr.GetCommandTab((int)docType, cmdGroup.Name);

                if (cmdTab == null)
                {
                    cmdTab = CmdMgr.AddCommandTab((int)docType, cmdGroup.Name);
                }

                if (cmdTab != null)
                {
                    var cmdIds = cmdGrp.Select(c => c.CmdId).ToArray();
                    var txtTypes = cmdGrp.Select(c => (int)c.TextType).ToArray();

                    var cmdBox = TryFindCommandTabBox(cmdTab, cmdIds);

                    if (cmdBox == null)
                    {
                        cmdBox = cmdTab.AddCommandTabBox();
                    }
                    else
                    {
                        if (!IsCommandTabBoxChanged(cmdBox, cmdIds, txtTypes))
                        {
                            continue;
                        }
                        else
                        {
                            ClearCommandTabBox(cmdBox);
                        }
                    }

                    if (!cmdBox.AddCommands(cmdIds, txtTypes))
                    {
                        throw new InvalidOperationException("Failed to add commands to commands tab box");
                    }
                }
                else
                {
                    throw new NullReferenceException("Failed to create command tab box");
                }
            }
        }

        private void CreateIcons(CommandGroup cmdGroup, CommandGroupSpec cmdBar, IIconsCreator iconsConv)
        {
            var mainIcon = cmdBar.Icon;

            if (mainIcon == null) 
            {
                mainIcon = Defaults.Icon;
            }

            IXImage[] iconList = null;

            if (cmdBar.Commands != null)
            {
                iconList = cmdBar.Commands.Select(c => c.Icon ?? Defaults.Icon).ToArray();
            }

            //NOTE: if commands are not used, main icon will fail if toolbar commands image list is not specified, so it is required to specify it explicitly

            if (CompatibilityUtils.SupportsHighResIcons(m_App.Sw, CompatibilityUtils.HighResIconsScope_e.CommandManager))
            {
                var iconsList = iconsConv.ConvertIcon(new CommandGroupHighResIcon(mainIcon));
                cmdGroup.MainIconList = iconsList;

                if (iconList != null && iconList.Any())
                {
                    cmdGroup.IconList = iconsConv.ConvertIconsGroup(
                        iconList.Select(i => new CommandGroupHighResIcon(i)).ToArray());
                }
                else
                {
                    cmdGroup.IconList = iconsList;
                }
            }
            else
            {
                var mainIconPath = iconsConv.ConvertIcon(new CommandGroupIcon(mainIcon));

                var smallIcon = mainIconPath[0];
                var largeIcon = mainIconPath[1];

                cmdGroup.SmallMainIcon = smallIcon;
                cmdGroup.LargeMainIcon = largeIcon;

                if (iconList != null && iconList.Any())
                {
                    var iconListPath = iconsConv.ConvertIconsGroup(iconList.Select(i => new CommandGroupIcon(i)).ToArray());
                    var smallIconList = iconListPath[0];
                    var largeIconList = iconListPath[1];

                    cmdGroup.SmallIconList = smallIconList;
                    cmdGroup.LargeIconList = largeIconList;
                }
                else
                {
                    cmdGroup.SmallIconList = smallIcon;
                    cmdGroup.LargeIconList = largeIcon;
                }
            }
        }

        private string GetMenuPath(CommandGroupSpec cmdBar)
        {
            var title = new StringBuilder();

            var parent = cmdBar.Parent;

            while (parent != null)
            {
                title.Insert(0, parent.Title + SUB_GROUP_SEPARATOR);
                parent = parent.Parent;
            }

            title.Append(cmdBar.Title);

            return title.ToString();
        }

        private CommandGroup GetRootCommandGroup(CommandGroupSpec cmdBar)
        {
            var root = cmdBar;

            while (root.Parent != null)
            {
                root = root.Parent;
            }

            return m_CommandBars.FirstOrDefault(b => b.Spec == root).CommandGroup;
        }

        private bool IsCommandTabBoxChanged(ICommandTabBox cmdBox, int[] cmdIds, int[] txtTypes)
        {
            object existingCmds;
            object existingTextStyles;
            cmdBox.GetCommands(out existingCmds, out existingTextStyles);

            if (existingCmds != null && existingTextStyles != null)
            {
                return !(existingCmds as int[]).SequenceEqual(cmdIds)
                    || !(existingTextStyles as int[]).SequenceEqual(txtTypes);
            }

            return true;
        }

        private CommandTabBox TryFindCommandTabBox(ICommandTab cmdTab, int[] cmdIds)
        {
            var cmdBoxesArr = cmdTab.CommandTabBoxes() as object[];

            if (cmdBoxesArr != null)
            {
                var cmdBoxes = cmdBoxesArr.Cast<CommandTabBox>().ToArray();

                var cmdBoxGroup = cmdBoxes.GroupBy(b =>
                {
                    object existingCmds;
                    object existingTextStyles;
                    b.GetCommands(out existingCmds, out existingTextStyles);

                    if (existingCmds is int[])
                    {
                        return ((int[])existingCmds).Intersect(cmdIds).Count();
                    }
                    else
                    {
                        return 0;
                    }
                }).OrderByDescending(g => g.Key).FirstOrDefault();

                if (cmdBoxGroup != null)
                {
                    if (cmdBoxGroup.Key > 0)
                    {
                        return cmdBoxGroup.FirstOrDefault();
                    }
                }

                return null;
            }

            return null;
        }
    }
}