[README.md](https://github.com/user-attachments/files/33831445/README.md)
<h1 align="center"><em>❄ 暖雪 · 同行 ❄</em></h1>

<p align="center"><strong>WarmSnowTogether</strong></p>

<p align="center"><strong>“雪落无声，杀戮有声——此去黄泉，与君同行”</strong></p>

 <div align="center">
  <img width="3200" height="2000" alt="wstpf" src="https://github.com/user-attachments/assets/3143add2-614e-4c3e-a1a1-2a865765d1e3" />
  <div style="text-align:left; display:inline-block;">
  </div>
</div>






<p align="center">
    <a href="https://github.com/BepInEx/BepInEx"><img alt="BepInEx" src="https://img.shields.io/badge/BepInEx-5.4.23.5-c9a227?style=for-the-badge&labelColor=0b0d12" /></a>
    <img alt="Unity" src="https://img.shields.io/badge/Unity-2020.3.22f1-d9e6f2?style=for-the-badge&labelColor=0b0d12" />
    <img alt="Protocol" src="https://img.shields.io/badge/Protocol-v3-8b0000?style=for-the-badge&labelColor=0b0d12" />
    <img alt="License" src="https://img.shields.io/badge/License-MIT-c9a227?style=for-the-badge&labelColor=0b0d12" />
    <br>
  <a href="#-缘起">缘起</a> &nbsp;&middot;&nbsp;
  <a href="#-功能一览">功能一览</a> &nbsp;&middot;&nbsp;
  <a href="#-已知限制">已知限制</a> &nbsp;&middot;&nbsp;
  <a href="#-版本">版本</a> &nbsp;&middot;&nbsp;
  <a href="#-开发者">开发者</a> &nbsp;&middot;&nbsp;
  <a href="#-致谢--免责声明">致谢&免责</a>
</p>


```text
                                                               .
                                                              / \
                                                             /   \
                                             .              /     \              .
                                            / \     .      /   .   \      .     / \
                                           /   \   / \    /         \    / \   /   \
                                          |     \ /   \  /           \  /   \ /    |
                                          |     _______\/_____________\/_______     |
                                               |      W A R M   S N O W        |
                                               |       T O G E T H E R         |
                                               |_______________________________|
      
      
      
                                                       *   .   *   .   *
                                                    .       S N O W       .
                                                       *   .   *   .   *
```

---

## ❄ 缘起

**WarmSnowTogether** 是《暖雪》的非官方合作联机 Mod。官方没有联机模式，这个 Mod 通过
**BepInEx + Harmony + Steam P2P**，把几个好友放进同一个"轮回"里：

- 一起进同一个场景，房主过门，其他人跟着走；
- 能看见彼此的角色替身，位置、朝向、动作实时同步；
- 怪物、伤害、掉落以房主为准，但每个人都能参与战斗、都能捡东西、都能正常使用。

> 本项目为免费、开源的爱好者作品，与《暖雪》官方及烂泥工作室无关，请支持正版。

---

## ⚔ 功能一览

| 模块 | 说明 |
| --- | --- |
| 🏔 **同境** | 房主决定场景，客户端自动加载同一关卡，过门跟随，偶发不同步会自动自愈 |
| 👤 **同影** | 克隆本地玩家骨架生成远端替身，四向朝向、左右镜像、动画跟随；纯视觉体，不会被怪打 |
| ⚔️ **同杀** | 怪物只归房主：生成、AI、血量、死亡、掉落均由房主计算，客户端只显示被动影子 |
| 🩸 **同刃** | 双方攻击都有效：客户端命中上报给房主，由房主执行真实伤害 |
| 💎 **同拾** | 掉落物以房主为标准，所有玩家可见、可捡、可使用；共享一份、先到先得 |
| 🧑‍🤝‍🧑 **同行** | 好友大厅最多 4 人；位置转发、加入补发快照、断线/退大厅自动清理 |

---

## 🚀 快速开始

### 0. 前置条件

| 项目 | 要求 |
| --- | --- |
| 游戏 | Steam 正版《暖雪》 |
| 框架 | **BepInEx 5.4.23.5**（[GitHub 仓库](https://github.com/BepInEx/BepInEx) · [文档](https://docs.bepinex.dev/)）；没有的话先给游戏装 BepInEx |
| 版本 | **两端必须使用同一份 `WarmSnowTogether.dll`**（协议 v3，不一致会被拒绝连接） |
| 网络 | Steam 在线（非离线模式），双方互为 Steam 好友 |

### 1. 安装

```text
WarmSnow\
└─ BepInEx\
   └─ plugins\
      └─ WarmSnowTogether\
         └─ WarmSnowTogether.dll      ← 放在这里
```

两端放同一份 DLL 即可，无需其它配置文件。

### 2. 联机

1. **房主**进佛堂（祠堂）→ 点击「**联机模式**」→ 自动创建好友大厅（FriendsOnly，最多 4 人）；
2. **房主**在弹出的 Steam 邀请窗口里邀请好友；
3. **客户端**接受邀请进入大厅 → Mod 自动连接房主；
4. **房主**开始一局 → 客户端自动加载同一场景，并在房主身边出生；
5. 左上角状态浮层显示 `ROLE / SteamID / Lobby / Status`，**F9** 显示或隐藏。

### 3. 退出

- 点击「退出联机」：断开 P2P、退出大厅、清理替身与掉落；
- 房主退出：客户端自动清理并回到单机状态；
- 回主菜单/切场景：替身与掉落随场景清空，重新进入会补发快照。

---

## 🧱 技术栈

| 层 | 技术 |
| --- | --- |
| 游戏 | Unity **2020.3.22f1** / .NET Framework 4.6.2 |
| 注入 | [**BepInEx 5.4.23.5**](https://github.com/BepInEx/BepInEx)（[文档](https://docs.bepinex.dev/)） |
| 补丁 | **HarmonyX / Harmony 2** |
| 网络 | **Steamworks.NET**：Steam 大厅（Matchmaking）+ P2P（ISteamNetworking） |
| 协议 | 手写二进制包，当前 **Protocol v3**（两端必须同版本 DLL） |
| 编译 | Visual Studio 2022 / MSBuild，`Release` → `WarmSnowTogether.dll` |

---

## 🏯 架构一瞥

```text
房主 HOST（唯一权威）                          客户端 CLIENT（影子世界）
────────────────────────────                  ────────────────────────────
场景 / 怪物 / 血量 / 死亡 / 掉落   ── 同步 ──►   同场景 + 被动替身 + 掉落镜像
           ▲                                        │
           └──────── 位置 / 伤害 / 拾取上报 ─────────┘
```

- **怪物只归房主**：客户端不刷怪、不结算死亡；
- **伤害双向生效**：客户端命中 → 房主真实结算 → 状态广播回来；
- **开局/加入补发快照**：新玩家或场景加载完成后补齐敌人与掉落；
- **一切以房主为准**：掉落、成就、存档都由房主驱动。

---

## 💎 物品同步（v1.3）

- 掉落物**以房主的物品为标准**：灵魂数值、魔剑词条、药水品质、技能书外观等都与房主一致；
- 所有玩家都能**看到、捡起、正常使用**；
- **共享一份、先到先得**：任何人捡走后，其它人那边的同一件东西立刻消失；
- 同步范围：**敌人死亡掉落** + **标准宝箱掉落**（关卡预设的静态掉落不同步）；
- 客户端在联机中不会乱写成就/图鉴/存档；本地宝箱由房主开启后掉落镜像提供。

详细限制见下方[已知限制](#limits)。

---

## 🔍 日志与排障

日志位置：

```text
WarmSnow\BepInEx\LogOutput.log
```

| 关键字 | 含义 |
| --- | --- |
| `[WST]` | 联机主流程（场景 / 敌人 / 玩家 / 清理） |
| `[WST][Loot]` | 物品同步（掉落生成、镜像、拾取） |
| `[SteamLobby]` | 大厅创建/加入/离开、Steam 错误原因 |
| `[SteamP2P]` | P2P 收发与会话状态 |
| `[UI]` / `[StatusOverlay]` | 联机按钮 / 状态浮层 |

常见问题：
- **点「联机模式」没反应**：多为 Steam 掉线或代理/hosts 干扰。确认 Steam 在线（好友列表、商店能打开）；关闭 Steam++/Watt Toolkit 的 hosts 加速；代理（Clash/SpeedCat 等）改直连或关 TUN；Steam 掉线后要**重启游戏**再点联机。

- **连不上 / 被拒绝**：`protocol mismatch` 表示两端 DLL 不是同一份；`CONNECT TIMEOUT` 表示房主没回应（房主掉线或不在大厅）。

- **看不到怪 / 掉落**：先确认客户端日志里场景 `buildIndex` 与房主一致；再看 `[WST][Loot]` 是否有镜像创建失败提示。

- **房主退出后客户端残留**：正常会自动清理；若卡住，重启联机即可。

- **状态浮层**：左上角常显角色、SteamID、大厅和最近错误，**F9** 切换显示。


---

## ✅ 自测清单

双人联机自测

**A. 连接**

- [ ] 房主创建大厅并弹出邀请窗
- [ ] 客户端进入大厅后显示已连接
- [ ] 版本不一致时被拒绝且不崩溃

**B. 场景**

- [ ] 房主进关卡，客户端自动进入同一场景
- [ ] 房主过门，客户端跟随
- [ ] 客户端出现在房主附近，没有卡墙

**C. 替身**

- [ ] 双方互相可见，位置/朝向大致同步
- [ ] 替身不会被怪打、没有血条
- [ ] 四向转身与攻击动作大致同步

**D. 怪物与伤害**

- [ ] 客户端没有本地刷出的怪
- [ ] 房主攻击 → 两端血条一起掉
- [ ] 客户端攻击 → 房主端掉血
- [ ] 房主杀怪 → 客户端替身消失、门正常打开

**E. 掉落物（v1.3）**

- [ ] 房主掉的东西客户端能看到
- [ ] 房主捡走 → 客户端消失
- [ ] 客户端捡走 → 客户端入包、房主端消失
- [ ] 灵魂数值、魔剑词条与房主一致
- [ ] 客户端捡东西后不会乱解锁成就/写存档

**F. 退出**

- [ ] 客户端退出后房主端替身消失

- [ ] 房主退出后客户端自动清理

- [ ] 退出后可重新建房/加入


---

## ⚠️ 已知限制

- 玩家 HP / 受伤暂不同步：敌人 AI 主要在房主端针对房主玩家，客户端角色基本不会被打；

- 元素 / 持续伤害 / 冰冻 / 燃烧等附加效果暂不透传，客户端攻击只透传数值伤害；

- 掉落为单份共享、先到先得；极端同时拾取理论上可能双份（毫秒级窗口）；

- 技能书品质/外观一致，但若游戏在拾取时才本地随机技能，双方拿到的具体技能可能不同；

- DLC / 梦魇 / 活动宝箱若不走标准宝箱逻辑，暂不同步其掉落；

- 客户端不能打开本地宝箱（防止重复掉落），但房主开出的东西会镜像到客户端；

- 打碎罐子、商店、佛像奖励维持各端本地逻辑；

- 暂不支持房主迁移 / 断线重连。


---

## 🕰 版本

| 版本 | 内容 |
| :---: | --- |
| v1.0 | 房主权威基础联机：场景、怪物、伤害、替身 |
| v1.1 | 稳定性大修：修复空引用刷屏、日志性能、断线清理、3+ 玩家问题 |
| v1.2 | 物品同步框架与协议 v3 |
| v1.3 | 物品同步完整版：掉落物可见、可捡、可用 |

---

## 👤 开发者

<p align="center">
  <a href="https://github.com/WAGVN/WarmSnowTogether/graphs/contributors">
    <img src="https://contrib.rocks/image?repo=WAGVN/WarmSnowTogether" alt="所有贡献者" />
  </a>
</p>


- 项目仓库：[WAGVN/WarmSnowTogether](https://github.com/WAGVN/WarmSnowTogether)

- 问题反馈：[Issues](https://github.com/WAGVN/WarmSnowTogether/issues)（欢迎附上 `LogOutput.log`）

- 想了解实现细节或**逆向相关内容**（例如用到的工具、分析思路），也欢迎在 Issue 里提出或联系作者交流；
  本项目的分析过程中使用了 **dnSpy / ILSpy / ildasm** 等工具。

---

## 💛 致谢 · 免责声明

- 感谢《暖雪》/ 烂泥工作室带来的这片雪与这个江湖；

- 感谢 **BepInEx**、**HarmonyX**、**Steamworks.NET** 等开源项目；

- 本项目采用 **MIT License**，免费、开源，请勿用于商业用途；

- 与《暖雪》官方无关，请支持正版；联机建议仅与 Steam 好友进行。


---

<div align="center">
  <h3>❄</h3>
  <p><b> 雪落之时，故人仍在。 </b></p>
  <p><b>愿你我同雪而行，共赴此劫。</b></p>

---

<p align="center">
  ⭐ 如果 <b><em>WarmSnowTogether</em></b> 对您有帮助，请点个 Star 让更多人看到它吧！⭐
</p>
---

<p align="center">
  ✨感谢访问 <b><em>WarmSnowTogether</em></b> ✨
</p>

<p align="center">
  <img src="https://visitor-badge.laobi.icu/badge?page_id=WAGVN.WarmSnowTogether&left_text=visitors&left_color=%2308608c&right_color=%231379b4&logo=github" alt="visitor badge"/>
</p>
