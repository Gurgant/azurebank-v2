# Figma MCP Setup & Locofy Export Guide
## AzureBank Design System

**Created**: 2025-12-17
**Updated**: 2026-01-02
**Status**: ✅ MCP SERVERS CONFIGURED - Ready for Frame Renaming & Locofy Export

---

## 🎯 CONTINUATION PROMPT

**Copy this prompt to continue in a new Claude Code session:**

```
Continue the AzureBank Figma-to-Locofy export preparation.

CURRENT STATUS:
- All 6 MCP servers are connected (figma-write-server, ClaudeTalkToFigma, html-to-design, etc.)
- Figma plugin for figma-mcp-write-server is running on port 8765
- 27 frames imported to Figma need renaming (remove timestamps)

IMMEDIATE TASKS:
1. Join figma-write-server channel (check Figma plugin UI for channel ID)
2. Rename all 27 frames using figma_nodes tool - remove timestamps, use semantic names
3. Fix internal layer naming (replace "Container", "Frame" with semantic names)
4. Configure Locofy MCP server (need token from Locofy Dashboard)
5. Verify Auto Layout on all frames
6. Final Locofy export readiness check

FRAME RENAME MAPPING (see project-docs/FIGMA-MCP-SETUP.md for full list):
- "Login_Mobile_2025-..." → "Login_Mobile"
- "Login_Desktop_2025-..." → "Login_Desktop"
- etc.

REFERENCE FILES:
- project-docs/FIGMA-MCP-SETUP.md - This setup guide
- C:\Dev\figma-mcp-write-server\SETUP-GUIDE.md - figma-write-server docs
- project-docs/FIGMA-SCREEN-MASTERPLAN.md - Screen specifications
```

---

## 1. Current MCP Server Status

### ✅ All 6 MCP Servers Connected

| Server | Transport | Port | Tools | Status |
|--------|-----------|------|-------|--------|
| **figma-write-server** | stdio → WebSocket | 8765 | 24 tools | ✅ Connected |
| **ClaudeTalkToFigma** | WebSocket | 3055 | ~30 tools | ✅ Connected |
| **html-to-design** | HTTP SSE | - | 2 tools | ✅ Connected |
| **figma-developer-mcp** | stdio | - | Read-only | ✅ Connected |
| **MCP_DOCKER** | HTTP | - | Browser + MCP mgmt | ✅ Connected |
| *(other)* | - | - | - | ✅ Connected |

---

## 2. figma-mcp-write-server Setup

### Installation Location
```
C:\Dev\figma-mcp-write-server\
```

### Node.js Requirement
- **Required**: Node.js 22.x
- **Managed via**: fnm (Fast Node Manager) v1.38.1
- **System Node preserved**: Node 24.x unchanged

### Start Server Manually (if needed)
```powershell
# Open NEW PowerShell terminal
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression
fnm use 22
cd C:\Dev\figma-mcp-write-server
npm start
```

### Quick Start Script
```powershell
# Or use the script:
C:\Dev\figma-mcp-write-server\start-server.ps1
```

### Claude Code MCP Configuration
Already added via:
```bash
claude mcp add figma-write-server --transport stdio -- powershell -Command "fnm env --shell powershell | Out-String | Invoke-Expression; fnm use 22; node C:/Dev/figma-mcp-write-server/dist/index.js"
```

### Figma Plugin Setup
1. Open **Figma Desktop** (NOT browser)
2. Go to **Plugins** → **Development** → **Import plugin from manifest**
3. Select: `C:\Dev\figma-mcp-write-server\figma-plugin\manifest.json`
4. Run: **Plugins** → **Development** → **Figma MCP Write Server**
5. Plugin shows "Connected to localhost:8765" when ready

---

## 3. Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Claude Code (AI Agent)                          │
└────────────┬────────────────────┬────────────────────┬─────────────────┘
             │                    │                    │
             ▼                    ▼                    ▼
┌────────────────────┐ ┌───────────────────┐ ┌──────────────────────────┐
│ figma-write-server │ │ ClaudeTalkToFigma │ │    html-to-design        │
│ (stdio → WS:8765)  │ │ (WS:3055)         │ │    (HTTP SSE)            │
│                    │ │                   │ │                          │
│ ✅ 24 Tools        │ │ ✅ ~30 Tools      │ │ ✅ import-url            │
│ ✅ WRITE ACCESS    │ │ ✅ Read/Write     │ │ ✅ import-html           │
│ ✅ Node Renaming   │ │ ❌ No Rename      │ │                          │
└─────────┬──────────┘ └─────────┬─────────┘ └────────────┬─────────────┘
          │                      │                        │
          ▼                      ▼                        ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                       Figma Desktop Application                          │
│                    (Plugin API via WebSocket)                            │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Available Tools (figma-write-server)

### Core Design (Node Renaming!)
- `figma_nodes` - **CREATE/UPDATE/DELETE/RENAME** shapes, frames, sections
- `figma_text` - Text operations
- `figma_fills` - Fill colors and gradients
- `figma_strokes` - Stroke styles
- `figma_effects` - Shadows, blur effects

### Layout & Positioning
- `figma_auto_layout` - Auto layout configuration
- `figma_constraints` - Responsive constraints
- `figma_alignment` - Align and distribute
- `figma_hierarchy` - Layer ordering, grouping

### Design System
- `figma_styles` - Create/manage styles
- `figma_components` - Component management
- `figma_instances` - Component instances
- `figma_variables` - Design tokens/variables
- `figma_fonts` - Font management

### Advanced Operations
- `figma_boolean_operations` - Union, subtract, intersect
- `figma_vectors` - Vector path manipulation

### Developer Tools
- `figma_dev_resources` - Dev handoff resources
- `figma_annotations` - Design annotations
- `figma_measurements` - Spacing measurements
- `figma_exports` - Export to PNG, SVG, PDF

### System
- `figma_plugin_status` - Check plugin connection
- `figma_pages` - Page management
- `figma_selection` - Selection management
- `figma_images` - Image operations

---

## 5. Frame Rename Mapping

### Current Frames (27 total) → Target Names

| Current Name (with timestamp) | Target Name |
|-------------------------------|-------------|
| `Login_Mobile_2025-...` | `Login_Mobile` |
| `Login_Desktop_2025-...` | `Login_Desktop` |
| `Register_Mobile_2025-...` | `Register_Mobile` |
| `Register_Desktop_2025-...` | `Register_Desktop` |
| `Dashboard_Mobile_2025-...` | `Dashboard_Mobile` |
| `Dashboard_Desktop_2025-...` | `Dashboard_Desktop` |
| `Transfer_Step1_Mobile_...` | `Transfer_Step1_Mobile` |
| `Transfer_Step1_Desktop_...` | `Transfer_Step1_Desktop` |
| `Transfer_Step2_Mobile_...` | `Transfer_Step2_Mobile` |
| `Transfer_Step2_Desktop_...` | `Transfer_Step2_Desktop` |
| `Transfer_Step3_Mobile_...` | `Transfer_Step3_Mobile` |
| `Transfer_Step3_Desktop_...` | `Transfer_Step3_Desktop` |
| `Transfer_Step4_Mobile_...` | `Transfer_Step4_Mobile` |
| `Transfer_Step4_Desktop_...` | `Transfer_Step4_Desktop` |
| `Transfer_Success_Mobile_...` | `Transfer_Success_Mobile` |
| `Transfer_Success_Desktop_...` | `Transfer_Success_Desktop` |
| `Deposit_Dialog_Mobile_...` | `Deposit_Dialog_Mobile` |
| `Deposit_Dialog_Desktop_...` | `Deposit_Dialog_Desktop` |
| `Withdraw_Dialog_Mobile_...` | `Withdraw_Dialog_Mobile` |
| `Withdraw_Dialog_Desktop_...` | `Withdraw_Dialog_Desktop` |
| `Transaction_History_Mobile_...` | `Transaction_History_Mobile` |
| `Transaction_History_Desktop_...` | `Transaction_History_Desktop` |
| `Account_Settings_Mobile_...` | `Account_Settings_Mobile` |
| `Account_Settings_Desktop_...` | `Account_Settings_Desktop` |
| `Error_State_Mobile_...` | `Error_State_Mobile` |
| `Loading_States_...` | `Loading_States` |
| `Empty_States_...` | `Empty_States` |

---

## 6. Locofy Export Preparation Checklist

### Pre-Export Requirements

| Requirement | Status | Notes |
|-------------|--------|-------|
| Frame names are clean (no timestamps) | ⏳ Pending | Use figma_nodes to rename |
| Layer names are semantic | ⏳ Pending | Replace "Frame", "Container" |
| Auto Layout applied | ⏳ To verify | Check all frames |
| Components properly named | ⏳ To verify | |
| No overlapping layers | ⏳ To verify | |
| Responsive constraints set | ⏳ To verify | |
| Design tokens consistent | ✅ Done | See section 8 |

### Locofy MCP Setup (Pending)
```bash
# Need token from: https://www.locofy.ai/dashboard
claude mcp add locofy-mcp -- npx @anthropic-ai/claude-code-mcp-starter locofy
```

---

## 7. Troubleshooting

### "Cannot connect to Figma" (figma-write-server)
1. Ensure Figma Desktop is open (not browser)
2. Run plugin: **Plugins** → **Development** → **Figma MCP Write Server**
3. Check plugin UI shows "Connected to localhost:8765"
4. Verify port 8765 is not blocked

### "Node version mismatch"
```powershell
# Activate fnm first:
fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression
fnm use 22
node --version  # Must show v22.x.x
```

### Port 8765 already in use
```powershell
# Find and kill process:
netstat -ano | findstr :8765
taskkill /PID <pid> /F
```

### fnm not found
```powershell
# Reinstall fnm:
winget install Schniz.fnm
# Restart terminal
```

### TypeScript build errors (if rebuilding)
The `npm run build` may show TypeScript errors about "examples" property. The pre-built `dist/` folder works fine - use it directly.

---

## 8. Design Tokens Quick Reference

### Brand Colors
| Token | Hex | RGB (0-1) | Usage |
|-------|-----|-----------|-------|
| Primary Blue | `#006DE2` | `0, 0.427, 0.886` | Main CTA, links |
| Primary Hover | `#004DA0` | `0, 0.302, 0.627` | Button hover |
| Primary Light | `#E6F0FC` | `0.902, 0.941, 0.988` | Selected BG |

### Semantic Colors
| Type | Background | Icon | Text |
|------|------------|------|------|
| Success/Deposit | `#E6F4EA` | `#34A853` | `#137333` |
| Error/Withdraw | `#FCE8E6` | `#EA4335` | `#C5221F` |
| Warning | `#FEF3E2` | `#F59E0B` | `#B45309` |
| Info | `#E0F2FE` | `#0EA5E9` | `#0369A1` |

### Screen Dimensions
- **Mobile**: 375×812px
- **Desktop**: 1440×900px

---

## 9. File Locations

| File | Purpose |
|------|---------|
| `C:\Dev\figma-mcp-write-server\` | MCP server installation |
| `C:\Dev\figma-mcp-write-server\dist\index.js` | Server entry point |
| `C:\Dev\figma-mcp-write-server\figma-plugin\manifest.json` | Figma plugin |
| `C:\Dev\figma-mcp-write-server\SETUP-GUIDE.md` | Server setup docs |
| `%APPDATA%\fnm\` | fnm Node versions |
| `project-docs/FIGMA-SCREEN-MASTERPLAN.md` | Screen specifications |
| `figma-html/` | Source HTML files |
| `design-tokens/tokens.json` | Machine-readable tokens |

---

## 10. Session History

### What Was Accomplished (2026-01-02)
1. ✅ Researched Locofy MCP requirements
2. ✅ Identified need for figma-mcp-write-server (node renaming capability)
3. ✅ Installed fnm (Fast Node Manager) v1.38.1
4. ✅ Installed Node.js 22.21.1 via fnm (without affecting system Node 24)
5. ✅ Cloned and configured figma-mcp-write-server at `C:\Dev\figma-mcp-write-server`
6. ✅ Built Figma plugin components
7. ✅ Connected Figma plugin to server (port 8765)
8. ✅ Added figma-write-server to Claude Code MCP configuration
9. ✅ Verified all 6 MCP servers connected
10. ✅ Created comprehensive documentation

### Pending Tasks
1. ⏳ Rename all 27 frames (remove timestamps)
2. ⏳ Fix internal layer naming
3. ⏳ Configure Locofy MCP (need token)
4. ⏳ Verify Auto Layout on all frames
5. ⏳ Final Locofy export readiness check

---

**Last Updated**: 2026-01-02 by Claude Code
