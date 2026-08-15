import { Fragment, useEffect, useMemo, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  Activity,
  ArrowDownToLine,
  ArrowUpFromLine,
  Check,
  ChevronRight,
  CircleHelp,
  Gauge,
  HardDrive,
  LayoutDashboard,
  LogOut,
  Network,
  Pause,
  Play,
  Plus,
  RefreshCw,
  Router,
  Search,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  Terminal,
  UserRound,
  X,
  Zap,
} from "@/components/ui-icons";
import toast from "react-hot-toast";

import {
  checkNodeStatus,
  createForward,
  createNode,
  createTunnel,
  deleteForward,
  forceDeleteForward,
  diagnoseForward,
  getAllUsers,
  getAdminWebSocketTicket,
  getForwardList,
  getNodeList,
  getNodeInstallCommand,
  getTunnelList,
  login,
  pauseForwardService,
  resumeForwardService,
  updateForward,
  updateTunnel,
  updateNode,
  deleteTunnel,
  diagnoseTunnel,
  getXuiInbounds,
} from "@/api";
import { isLoggedIn } from "@/utils/auth";
import { safeLogout } from "@/utils/logout";
import SettingsView from "@/pages/SettingsView";
import UsersView from "@/pages/UsersView";
import XuiView from "@/pages/XuiView";
import type { ApiItem, Creator, View } from "@/types/app";

type NodeStats = {
  cpu?: number;
  memory?: number;
  updatedAt: number;
};

type DiagnosticTarget = {
  label?: string;
  target: string;
  success: boolean;
  reachable?: boolean;
  statusCode?: number;
  finalUrl?: string;
  redirected?: boolean;
  duration?: number;
  errorType?: string;
  averageTime?: number;
  packetLoss?: number;
  error?: string;
};

type DiagnosticState = {
  kind: "forward" | "tunnel";
  resource: ApiItem;
  status: "running" | "success" | "error";
  runId: string;
  response: ApiItem | null;
  error?: string;
};

type ImportForwardRow = {
  line: number;
  raw: string;
  target: string;
  name: string;
  inPort?: number;
  outPort?: number;
};

type ImportForwardOutcome = {
  line: number;
  raw: string;
  ok: boolean;
  message: string;
};

const navItems: Array<{ path: View; label: string; icon: typeof LayoutDashboard; note: string }> = [
  { path: "dashboard", label: "总览", icon: LayoutDashboard, note: "运行态势" },
  { path: "forwards", label: "转发服务", icon: Network, note: "流量入口" },
  { path: "nodes", label: "节点管理", icon: Router, note: "在线设备" },
  { path: "tunnels", label: "隧道编排", icon: SlidersHorizontal, note: "链路拓扑" },
  { path: "users", label: "用户管理", icon: UserRound, note: "账号与额度" },
  { path: "xui", label: "3x-ui 集成", icon: Router, note: "入站同步" },
  { path: "settings", label: "面板设置", icon: Settings2, note: "访问与安全" },
];

const formatBytes = (value: number | undefined | null) => {
  const bytes = Number(value || 0);
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes < 1024 ** 4) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  return `${(bytes / 1024 ** 4).toFixed(2)} TB`;
};

const trafficUsage = (item: ApiItem) => Number(item.inFlow || 0) + (Number(item.tunnelFlow || 2) === 1 ? 0 : Number(item.outFlow || 0));

type TrafficSummary = {
  ingress: number;
  egress: number;
  total: number;
  forwardCount: number;
};

const nodeTrafficSummary = (nodeId: number, tunnels: ApiItem[], forwards: ApiItem[]): TrafficSummary => {
  const tunnelById = new Map(tunnels.map((tunnel) => [Number(tunnel.id), tunnel]));
  return forwards.reduce<TrafficSummary>((summary, forward) => {
    const tunnel = tunnelById.get(Number(forward.tunnelId));
    if (!tunnel) return summary;
    const used = trafficUsage(forward);
    const isIngress = Number(tunnel.inNodeId) === Number(nodeId);
    const isEgress = Number(tunnel.outNodeId) === Number(nodeId);
    if (!isIngress && !isEgress) return summary;
    return {
      ingress: summary.ingress + (isIngress ? used : 0),
      egress: summary.egress + (isEgress ? used : 0),
      total: summary.total + used,
      forwardCount: summary.forwardCount + 1,
    };
  }, { ingress: 0, egress: 0, total: 0, forwardCount: 0 });
};

const reverseNodeRoles = (nodeId: number, tunnels: ApiItem[]) => {
  const roles: string[] = [];
  if (tunnels.some((tunnel) => Number(tunnel.type) === 3 && Number(tunnel.inNodeId) === nodeId)) roles.push("公网入口节点");
  if (tunnels.some((tunnel) => Number(tunnel.type) === 3 && Number(tunnel.outNodeId) === nodeId)) roles.push("内网 Windows 节点");
  return roles;
};

const formatDate = (value: number | string | undefined) => {
  if (!value) return "-";
  const date = new Date(Number(value));
  return Number.isNaN(date.getTime()) ? "-" : date.toLocaleString("zh-CN", { hour12: false });
};

const asRecord = (value: unknown): ApiItem => {
  if (typeof value === "string") {
    try {
      return asRecord(JSON.parse(value));
    } catch {
      return {};
    }
  }
  return value && typeof value === "object" && !Array.isArray(value) ? value as ApiItem : {};
};

const diagnosticTargets = (diagnostic: DiagnosticState): DiagnosticTarget[] => {
  if (!diagnostic.response) return [];
  if (diagnostic.kind === "forward") {
    const results = Array.isArray(diagnostic.response.data?.results) ? diagnostic.response.data.results : [];
    return results.map((result: unknown) => {
      const item = asRecord(result);
      const target = item.ip || item.targetIp ? `${item.ip || item.targetIp}:${item.port || item.targetPort || "-"}` : "未知目标";
      return {
        target,
        success: item.success === true,
        averageTime: Number.isFinite(Number(item.averageTime)) ? Number(item.averageTime) : undefined,
        packetLoss: Number.isFinite(Number(item.packetLoss)) ? Number(item.packetLoss) : undefined,
        error: item.errorMessage || item.message,
      };
    });
  }

  const stagedResults = Array.isArray(diagnostic.response.data?.results) ? diagnostic.response.data.results : [];
  if (stagedResults.length > 0) {
    return stagedResults.map((result: unknown) => {
      const item = asRecord(result);
      const data = asRecord(item.data);
      return {
        label: typeof item.label === "string" ? item.label : undefined,
        target: item.target || item.label || "诊断步骤",
        success: item.success === true,
        averageTime: Number.isFinite(Number(data.averageTime)) ? Number(data.averageTime) : undefined,
        packetLoss: Number.isFinite(Number(data.packetLoss)) ? Number(data.packetLoss) : undefined,
        error: item.error || data.errorMessage,
      };
    });
  }

  const envelope = asRecord(diagnostic.response.data);
  const result = asRecord(envelope.data);
  if (Object.keys(result).length > 0) {
    const isHttpProbe = envelope.probeMode !== "tcp-compat" && (typeof result.statusCode === "number" || typeof result.reachable === "boolean" || typeof result.finalUrl === "string");
    const target = isHttpProbe
      ? result.url || envelope.targetUrl || `${diagnostic.resource.outIp || "出口节点"}`
      : result.ip || result.targetIp ? `${result.ip || result.targetIp}:${result.port || result.targetPort || "-"}` : `${diagnostic.resource.outIp || "出口节点"}:443`;
    return [{
      target,
      success: result.success === true,
      reachable: result.reachable === true,
      statusCode: Number.isFinite(Number(result.statusCode)) && Number(result.statusCode) > 0 ? Number(result.statusCode) : undefined,
      finalUrl: result.finalUrl,
      redirected: result.redirected === true,
      duration: Number.isFinite(Number(result.duration)) ? Number(result.duration) : undefined,
      errorType: result.errorType,
      averageTime: Number.isFinite(Number(result.averageTime)) ? Number(result.averageTime) : undefined,
      packetLoss: Number.isFinite(Number(result.packetLoss)) ? Number(result.packetLoss) : undefined,
      error: result.errorMessage || result.message || envelope.message,
    }];
  }
  return [{
    target: envelope.targetUrl || `${diagnostic.resource.outIp || "出口节点"}`,
    success: envelope.success === true,
    error: envelope.message || "节点没有返回探测结果",
  }];
};

const parseForwardImport = (text: string, reverseRelay = false): { rows: ImportForwardRow[]; errors: ImportForwardOutcome[] } => {
  const rows: ImportForwardRow[] = [];
  const errors: ImportForwardOutcome[] = [];
  text.split(/\r?\n/).forEach((rawLine, index) => {
    const raw = rawLine.trim();
    if (!raw) return;
    const parts = raw.split("|").map((part) => part.trim());
    if (parts.length !== 3) {
      errors.push({ line: index + 1, raw, ok: false, message: reverseRelay ? "格式应为：目标地址|映射名称|公网入口端口" : "格式应为：目标地址|转发名称|入口端口" });
      return;
    }

    const [target, name, portText] = parts;
    const closeBracket = target.startsWith("[") ? target.indexOf("]") : -1;
    const separator = closeBracket > 0 ? target.indexOf(":", closeBracket) : target.lastIndexOf(":");
    const targetPort = separator > 0 ? Number(target.slice(separator + 1)) : 0;
    const inPort = portText ? Number(portText) : undefined;
    if (!target || separator <= 0 || !Number.isInteger(targetPort) || targetPort < 1 || targetPort > 65535) {
      errors.push({ line: index + 1, raw, ok: false, message: "目标地址必须包含有效端口，例如 10.0.0.5:443" });
      return;
    }
    if (!name) {
      errors.push({ line: index + 1, raw, ok: false, message: "转发名称不能为空" });
      return;
    }
    if (inPort !== undefined && (!Number.isInteger(inPort) || inPort < 1 || inPort > 65535)) {
      errors.push({ line: index + 1, raw, ok: false, message: `${reverseRelay ? "公网入口端口" : "入口端口"}必须是 1-65535 的整数，留空可自动分配` });
      return;
    }
    rows.push({ line: index + 1, raw, target, name, inPort });
  });
  return { rows, errors };
};

function StatusDot({ status }: { status: boolean }) {
  return <span className={`status-dot ${status ? "is-online" : "is-offline"}`} aria-label={status ? "在线" : "离线"} />;
}

function LoginPage() {
  const navigate = useNavigate();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [totpCode, setTotpCode] = useState("");
  const [totpRequired, setTotpRequired] = useState(false);
  const [loading, setLoading] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!username.trim() || !password) {
      toast.error("请输入账号和密码");
      return;
    }
    setLoading(true);
    try {
      const response = await login({ username: username.trim(), password, captchaId: "", totpCode: totpCode.trim() || undefined });
      if (response.code !== 0) {
        if (response.data?.requiresTotp) setTotpRequired(true);
        toast.error(response.msg || "登录失败");
        return;
      }
      localStorage.setItem("token", response.data.token);
      localStorage.setItem("role_id", String(response.data.role_id));
      localStorage.setItem("name", response.data.name);
      localStorage.setItem("admin", String(response.data.role_id === 0));
      navigate("/dashboard", { replace: true });
    } catch {
      toast.error("无法连接面板服务");
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="login-screen">
      <div className="login-grid" />
      <section className="login-panel">
        <div className="brand-mark"><Zap size={18} strokeWidth={2.8} /></div>
        <p className="eyebrow">PRIVATE RELAY CONTROL</p>
        <h1>RelayForge</h1>
        <p className="login-copy">私人转发基础设施的统一操作台</p>
        <form onSubmit={submit} className="login-form">
          <label>管理员账号<input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" placeholder="输入账号" /></label>
          <label>访问密码<input value={password} onChange={(e) => setPassword(e.target.value)} type="password" autoComplete="current-password" placeholder="输入密码" /></label>
          {totpRequired && <label>2FA 验证码<input value={totpCode} onChange={(e) => setTotpCode(e.target.value.replace(/\D/g, "").slice(0, 6))} inputMode="numeric" autoComplete="one-time-code" placeholder="输入认证器中的 6 位验证码" /></label>}
          <button className="button button-primary button-wide" disabled={loading}>{loading ? "正在验证..." : "进入控制台"}<ChevronRight size={17} /></button>
        </form>
        <div className="login-foot"><ShieldCheck size={14} /> AES-256-GCM 通信链路已启用</div>
      </section>
      <aside className="login-aside">
        <div className="signal-card">
          <div className="signal-header"><span>CONTROL PLANE</span><span className="signal-live"><i /> READY</span></div>
          <div className="signal-line"><span /><span /><span /><span /><span /><span /><span /></div>
          <div className="signal-values"><strong>/.panel</strong><span>encrypted session</span></div>
        </div>
        <p>Keep every route observable.<br />Make every change reversible.</p>
      </aside>
    </main>
  );
}

function Workspace() {
  const location = useLocation();
  const navigate = useNavigate();
  const [nodes, setNodes] = useState<ApiItem[]>([]);
  const [tunnels, setTunnels] = useState<ApiItem[]>([]);
  const [forwards, setForwards] = useState<ApiItem[]>([]);
  const [users, setUsers] = useState<ApiItem[]>([]);
  const [xuiInbounds, setXuiInbounds] = useState<ApiItem[]>([]);
  const [nodeStats, setNodeStats] = useState<Record<string, NodeStats>>({});
  const [, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [query, setQuery] = useState("");
  const [moduleQuery, setModuleQuery] = useState("");
  const [openTabs, setOpenTabs] = useState<View[]>(["dashboard"]);
  const [selectedNode, setSelectedNode] = useState<ApiItem | null>(null);
  const [selectedTunnel, setSelectedTunnel] = useState<ApiItem | null>(null);
  const [selectedForward, setSelectedForward] = useState<ApiItem | null>(null);
  const [diagnostic, setDiagnostic] = useState<DiagnosticState | null>(null);
  const [importingForwards, setImportingForwards] = useState(false);
  const [activity, setActivity] = useState<Array<{ title: string; detail: string; time: string; tone: string }>>([]);
  const [creator, setCreator] = useState<Creator>(null);

  const activeView = (location.pathname.replace("/", "") || "dashboard") as View;
  const isAdmin = localStorage.getItem("admin") === "true" || localStorage.getItem("role_id") === "0";
  const displayName = localStorage.getItem("name") || "admin";

  useEffect(() => {
    if (navItems.some((item) => item.path === activeView)) setOpenTabs((current) => current.includes(activeView) ? current : [...current, activeView]);
  }, [activeView]);

  const openView = (view: View) => {
    setOpenTabs((current) => current.includes(view) ? current : [...current, view]);
    navigate(`/${view}`);
  };
  const closeTab = (view: View) => {
    const next = openTabs.filter((tab) => tab !== view);
    const remaining = next.length ? next : ["dashboard" as View];
    setOpenTabs(remaining);
    if (activeView === view) navigate(`/${remaining[remaining.length - 1]}`);
  };

  const loadData = async (quiet = false) => {
    if (!quiet) setRefreshing(true);
    try {
      const [nodeResponse, tunnelResponse, forwardResponse, xuiResponse] = await Promise.all([getNodeList(), getTunnelList(), getForwardList(), getXuiInbounds()]);
      if (nodeResponse.code === 0) setNodes(nodeResponse.data || []);
      if (tunnelResponse.code === 0) setTunnels(tunnelResponse.data || []);
      if (forwardResponse.code === 0) setForwards(forwardResponse.data || []);
      if (xuiResponse.code === 0) setXuiInbounds(xuiResponse.data || []);
      if (isAdmin) {
        const userResponse = await getAllUsers({ page: 1, size: 100 });
        if (userResponse.code === 0) setUsers(Array.isArray(userResponse.data) ? userResponse.data : userResponse.data?.list || []);
      }
      if (!quiet) toast.success("数据已同步");
    } catch {
      if (!quiet) toast.error("同步失败，请检查后端连接");
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  };

  useEffect(() => { void loadData(true); }, []);

  useEffect(() => {
    const token = localStorage.getItem("token");
    if (!token) return;
    let socket: WebSocket | null = null;
    let disposed = false;
    void getAdminWebSocketTicket().then((response) => {
      if (disposed || response.code !== 0 || !response.data?.ticket) return;
      const protocol = window.location.protocol === "https:" ? "wss" : "ws";
      socket = new WebSocket(`${protocol}://${window.location.host}/system-info?ticket=${encodeURIComponent(response.data.ticket)}&type=2`);
      socket.onmessage = (event) => {
        try {
          const message = JSON.parse(event.data);
          const nodeId = String(message.id ?? "");
          if (!nodeId) return;
          if (message.type === "status") {
            setNodes((current) => current.map((node) => String(node.id) === nodeId ? { ...node, status: Number(message.data) } : node));
          }
          if (message.type === "info") {
            const raw = typeof message.data === "string" ? JSON.parse(message.data) : message.data;
            setNodeStats((current) => ({ ...current, [nodeId]: { cpu: Number(raw.cpu_usage ?? raw.cpu ?? 0), memory: Number(raw.memory_usage ?? raw.memory ?? 0), updatedAt: Date.now() } }));
          }
        } catch { /* ignore malformed telemetry */ }
      };
    });
    return () => { disposed = true; socket?.close(); };
  }, []);

  const recordActivity = (title: string, detail: string, tone = "teal") => {
    setActivity((current) => [{ title, detail, tone, time: "刚刚" }, ...current].slice(0, 5));
  };

  const toggleForward = async (forward: ApiItem) => {
    const enabled = Number(forward.status) === 1;
    const response = enabled ? await pauseForwardService(forward.id) : await resumeForwardService(forward.id);
    if (response.code !== 0) {
      toast.error(response.msg || "操作失败");
      return;
    }
    setForwards((current) => current.map((item) => item.id === forward.id ? { ...item, status: enabled ? 0 : 1 } : item));
    recordActivity(enabled ? "已暂停转发" : "已恢复转发", `${forward.name} · ${forward.entryIp || forward.inIp}:${forward.inPort}`, enabled ? "amber" : "teal");
    toast.success(enabled ? "服务已暂停" : "服务已恢复");
  };

  const runDiagnostic = async (kind: DiagnosticState["kind"], resource: ApiItem) => {
    const runId = `${kind}-${resource.id}-${Date.now()}`;
    if (kind === "tunnel") setSelectedTunnel(null);
    setDiagnostic({ kind, resource, status: "running", runId, response: null });
    try {
      const response = kind === "forward" ? await diagnoseForward(Number(resource.id)) : await diagnoseTunnel(Number(resource.id));
      if (response.code !== 0) {
        setDiagnostic((current) => current?.runId === runId ? { ...current, status: "error", error: response.msg || "诊断请求失败" } : current);
        return;
      }
      setDiagnostic((current) => current?.runId === runId ? { ...current, status: "success", response } : current);
      recordActivity(kind === "forward" ? "完成链路诊断" : "完成隧道诊断", resource.name || `资源-${resource.id}`, "blue");
    } catch {
      setDiagnostic((current) => current?.runId === runId ? { ...current, status: "error", error: "无法连接诊断服务，请检查后端和节点状态" } : current);
    }
  };

  const removeForward = async (forward: ApiItem) => {
    if (!window.confirm(`确定删除转发“${forward.name || forward.id}”吗？`)) return;
    let response = await deleteForward(Number(forward.id));
    if (response.code !== 0 && isAdmin) response = await forceDeleteForward(Number(forward.id));
    if (response.code !== 0) {
      toast.error(response.msg || "删除失败");
      return;
    }
    setForwards((current) => current.filter((item) => Number(item.id) !== Number(forward.id)));
    recordActivity("删除转发服务", `${forward.name || forward.id} · ${forward.entryIp || forward.inIp}:${forward.inPort}`, "amber");
    toast.success("转发服务已删除");
  };
  const diagnose = (forward: ApiItem) => { void runDiagnostic("forward", forward); };

  const refreshNode = async (node?: ApiItem) => {
    const response = await checkNodeStatus(node?.id);
    if (response.code === 0) {
      setNodes(response.data || []);
      recordActivity("刷新节点状态", node?.name || "全部节点", "blue");
      toast.success("状态已刷新");
    }
  };

  const installNode = async (node: ApiItem, platform: "linux" | "windows" = "linux") => {
    const response = await getNodeInstallCommand(Number(node.id), platform);
    if (response.code !== 0 || !response.data) {
      toast.error(response.msg || "获取安装命令失败");
      return;
    }

    const command = String(response.data);
    try {
      await navigator.clipboard.writeText(command);
    } catch {
      const textarea = document.createElement("textarea");
      textarea.value = command;
      textarea.style.position = "fixed";
      textarea.style.opacity = "0";
      document.body.appendChild(textarea);
      textarea.select();
      const copied = document.execCommand("copy");
      textarea.remove();
      if (!copied) {
        toast.error("无法自动复制，请检查浏览器剪贴板权限");
        return;
      }
    }

    recordActivity(`生成 ${platform === "windows" ? "Windows" : "Linux"} 节点安装命令`, node.name || `Node-${node.id}`, "teal");
    toast.success(`${platform === "windows" ? "Windows PowerShell" : "Linux"} 安装命令已复制到剪贴板`);
  };

  const logout = () => { safeLogout(); navigate("/", { replace: true }); };
  const createResource = async (kind: Exclude<Creator, null>, values: Record<string, string>) => {
    const numericKeys = ["inNodeId", "outNodeId", "tunnelId", "xuiInboundId", "inPort", "outPort", "type", "flowType", "flowLimitGb", "flow", "trafficRatio", "speedLimitKbps"];
    const payload = Object.fromEntries(Object.entries(values).flatMap(([key, value]) => {
      if (key === "autoAssignPort" || (key === "inPort" || key === "outPort") && !value.trim()) return [];
      return [[key, numericKeys.includes(key) ? Number(value) : value]];
    }));
    const response = kind === "nodes" ? await createNode(payload) : kind === "tunnels" ? await createTunnel(payload) : await createForward(payload);
    if (response.code !== 0) {
      toast.error(response.msg || "创建失败");
      return false;
    }
    setCreator(null);
    recordActivity(kind === "nodes" ? "接入节点配置" : kind === "tunnels" ? "创建隧道配置" : "创建转发服务", String(values.name || "新资源"), "teal");
    await loadData(true);
    toast.success("配置已创建");
    return true;
  };
  const saveNode = async (values: Record<string, string>) => {
    if (!selectedNode) return false;
    const response = await updateNode({
      id: Number(selectedNode.id),
      name: values.name.trim(),
      serverIp: values.serverIp.trim(),
      ip: values.ip.trim(),
      portRange: values.portRange.trim(),
      portSta: Number(selectedNode.portSta || 0),
      portEnd: Number(selectedNode.portEnd || 0),
      http: Number(selectedNode.http || 0),
      tls: Number(selectedNode.tls || 0),
      socks: Number(selectedNode.socks || 0),
    });
    if (response.code !== 0) {
      toast.error(response.msg || "节点保存失败");
      return false;
    }
    setSelectedNode(null);
    await loadData(true);
    recordActivity("更新节点配置", values.name, "teal");
    toast.success("节点配置已保存");
    return true;
  };
  const importForwards = async (tunnelId: number, rows: ImportForwardRow[]) => {
    const outcomes: ImportForwardOutcome[] = [];
    for (const row of rows) {
      const response = await createForward({ name: row.name, tunnelId, remoteAddr: row.target, inPort: row.inPort, outPort: row.outPort, strategy: "fifo" });
      outcomes.push({ line: row.line, raw: row.raw, ok: response.code === 0, message: response.code === 0 ? (response.data?.existing ? "已存在，跳过重复创建" : "已创建") : response.msg || "创建失败" });
    }
    await loadData(true);
    const successCount = outcomes.filter((outcome) => outcome.ok).length;
    if (successCount > 0) recordActivity("批量导入转发", `${successCount} 条转发`, "teal");
    return outcomes;
  };
  const saveTunnel = async (values: Record<string, string>) => {
    if (!selectedTunnel) return false;
    const response = await updateTunnel({
      id: Number(selectedTunnel.id), name: values.name.trim(), flowType: Number(values.flowType || 2), flowLimitGb: Number(values.flowLimitGb || 0),
      trafficRatio: Number(values.trafficRatio || 1), speedLimitKbps: Number(values.speedLimitKbps || 0), protocol: values.protocol,
      anyTlsPassword: values.anyTlsPassword || "", tcpListenAddr: values.tcpListenAddr || "[::]",
      udpListenAddr: values.udpListenAddr || "[::]", interfaceName: values.interfaceName || "",
    });
    if (response.code !== 0) { toast.error(response.msg || "隧道保存失败"); return false; }
    setSelectedTunnel(null);
    await loadData(true);
    recordActivity("更新隧道配置", values.name, "teal");
    toast.success("隧道已保存并同步");
    return true;
  };
  const saveForward = async (values: Record<string, string>) => {
    if (!selectedForward) return false;
    const response = await updateForward({
      id: Number(selectedForward.id), name: values.name.trim(), remoteAddr: values.remoteAddr.trim(),
      strategy: values.strategy, interfaceName: values.interfaceName.trim(), flow: Number(values.flow || 0),
      inPort: Number(values.inPort),
      tunnelId: Number(selectedForward.tunnelId),
    });
    if (response.code !== 0) { toast.error(response.msg || "转发保存失败"); return false; }
    setSelectedForward(null);
    await loadData(true);
    recordActivity("更新转发额度", values.name, "teal");
    toast.success("转发已保存");
    return true;
  };
  const inspectTunnel = (tunnel: ApiItem) => { void runDiagnostic("tunnel", tunnel); };
  const removeTunnel = async (tunnel: ApiItem) => {
    const count = forwards.filter((forward) => Number(forward.tunnelId) === Number(tunnel.id)).length;
    if (count > 0) { toast.error(`该隧道还有 ${count} 条转发，请先删除或迁移转发`); return; }
    if (!window.confirm(`确定删除隧道“${tunnel.name || tunnel.id}”吗？`)) return;
    const response = await deleteTunnel(Number(tunnel.id));
    if (response.code !== 0) { toast.error(response.msg || "隧道删除失败"); return; }
    await loadData(true);
    recordActivity("删除隧道", tunnel.name || `Tunnel-${tunnel.id}`, "amber");
    toast.success("隧道已删除");
  };
  const filteredForwards = useMemo(() => forwards.filter((item) => `${item.name} ${item.remoteAddr} ${item.tunnelName} ${item.xuiInboundName}`.toLowerCase().includes(query.toLowerCase())), [forwards, query]);
  const onlineNodes = nodes.filter((node) => Number(node.status) === 1).length;
  const activeForwards = forwards.filter((forward) => Number(forward.status) === 1).length;
  const totalUp = forwards.reduce((sum, forward) => sum + Number(forward.inFlow || 0), 0);
  const totalDown = forwards.reduce((sum, forward) => sum + (Number(forward.tunnelFlow || 2) === 1 ? 0 : Number(forward.outFlow || 0)), 0);

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="sidebar-brand"><div className="brand-mark">RF</div><div><small>RELAYFORGE</small><strong>Admin Console</strong></div></div>
        <button className="workspace-switcher" type="button"><span className="workspace-glyph"><LayoutDashboard size={14} /></span><strong>Workspace</strong><span className="workspace-count">{forwards.length}</span></button>
        <label className="module-search"><Search size={15} /><input value={moduleQuery} onChange={(event) => setModuleQuery(event.target.value)} placeholder="搜索模块" /></label>
        <div className="sidebar-section-label">运行总览</div>
        <nav className="main-nav">
          {navItems.filter((item) => (isAdmin || item.path !== "users") && `${item.label} ${item.note}`.toLowerCase().includes(moduleQuery.toLowerCase())).map(({ path, label, icon: Icon, note }) => <button key={path} className={activeView === path ? "nav-item active" : "nav-item"} onClick={() => openView(path)}><span className="nav-icon"><Icon size={16} /></span><span><strong>{label}</strong><small>{note}</small></span>{activeView === path && <ChevronRight size={15} />}</button>)}
        </nav>
        <div className="sidebar-section-label sidebar-section-secondary">系统</div>
        <div className="sidebar-bottom">
          <div className="secure-note"><ShieldCheck size={16} /><span><strong>SECURE CHANNEL</strong><small>Agent encryption online</small></span><i /></div>
          <button className="user-row" onClick={logout}><span className="avatar"><UserRound size={15} /></span><span><strong>{displayName}</strong><small>{isAdmin ? "Administrator" : "Operator"}</small></span><LogOut size={15} /></button>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar"><div><span className="breadcrumb">RELAYFORGE / {activeView.toUpperCase()}</span><h1>{navItems.find((item) => item.path === activeView)?.label || "总览"}</h1></div><div className="topbar-actions"><div className="system-state"><i />系统正常 <span>·</span> {onlineNodes}/{nodes.length || 0} 节点在线</div><button className="icon-button" title="刷新所有数据" onClick={() => void loadData()} disabled={refreshing}><RefreshCw size={17} className={refreshing ? "spin" : ""} /></button><button className="button button-primary" onClick={() => setCreator("forwards")}><Plus size={16} /> 新建转发</button></div></header>

        <div className="workspace-tabs" role="tablist" aria-label="已打开模块">{openTabs.map((tab) => { const item = navItems.find((navItem) => navItem.path === tab)!; const Icon = item.icon; return <button key={tab} type="button" role="tab" aria-selected={tab === activeView} className={tab === activeView ? "workspace-tab active" : "workspace-tab"} onClick={() => openView(tab)}><Icon size={15} /><strong>{item.label}</strong><span className="tab-close" role="button" aria-label={`关闭${item.label}`} onClick={(event) => { event.stopPropagation(); closeTab(tab); }}><X size={13} /></span></button>; })}</div>
        <div className="content-area">
          <ConsoleOverview view={activeView} tabs={openTabs.length} nodes={nodes.length} forwards={forwards.length} tunnels={tunnels.length} activeForwards={activeForwards} isAdmin={isAdmin} />
          {activeView === "dashboard" && <DashboardView nodes={nodes} tunnels={tunnels} forwards={forwards} users={users} onlineNodes={onlineNodes} activeForwards={activeForwards} totalUp={totalUp} totalDown={totalDown} activity={activity} onNavigate={openView} onCreate={setCreator} onToggle={toggleForward} onDiagnose={diagnose} onEdit={setSelectedForward} />}
          {activeView === "forwards" && <ForwardsView forwards={filteredForwards} query={query} setQuery={setQuery} onCreate={() => setCreator("forwards")} onImport={() => setImportingForwards(true)} onToggle={toggleForward} onDiagnose={diagnose} onDelete={removeForward} onEdit={setSelectedForward} onRefresh={() => void loadData()} />}
          {activeView === "nodes" && <NodesView nodes={nodes} tunnels={tunnels} forwards={forwards} nodeStats={nodeStats} onCreate={() => setCreator("nodes")} onEdit={setSelectedNode} onRefresh={refreshNode} onInstall={installNode} onReload={() => void loadData()} />}
          {activeView === "tunnels" && <TunnelsView tunnels={tunnels} forwards={forwards} nodes={nodes} onCreate={() => setCreator("tunnels")} onManage={setSelectedTunnel} onDiagnose={inspectTunnel} onDelete={removeTunnel} />}
          {activeView === "users" && isAdmin && <UsersView users={users} onChanged={() => void loadData(true)} />}
          {activeView === "users" && !isAdmin && <div className="panel empty-large"><ShieldCheck size={24} /><h3>没有访问权限</h3><p>用户管理仅对管理员开放。</p></div>}
          {activeView === "xui" && <XuiView onChanged={() => void loadData(true)} />}
          {activeView === "settings" && <SettingsView />}
        </div>
      </main>
      {diagnostic && <DiagnosticDrawer diagnostic={diagnostic} onClose={() => setDiagnostic(null)} />}
      {importingForwards && <ImportForwardModal tunnels={tunnels} onClose={() => setImportingForwards(false)} onSubmit={importForwards} />}
      {creator && <CreateModal kind={creator} nodes={nodes} tunnels={tunnels} xuiInbounds={xuiInbounds} onClose={() => setCreator(null)} onSubmit={createResource} />}
      {selectedNode && <NodeEditModal node={selectedNode} onClose={() => setSelectedNode(null)} onSubmit={saveNode} />}
      {selectedForward && <ForwardEditModal forward={selectedForward} onClose={() => setSelectedForward(null)} onSubmit={saveForward} onDiagnose={() => void diagnose(selectedForward)} />}
      {selectedTunnel && <TunnelEditModal tunnel={selectedTunnel} onClose={() => setSelectedTunnel(null)} onSubmit={saveTunnel} onDiagnose={() => void inspectTunnel(selectedTunnel)} onDelete={() => void removeTunnel(selectedTunnel)} />}
    </div>
  );
}

function ConsoleOverview({ view, tabs, nodes, forwards, tunnels, activeForwards, isAdmin }: { view: View; tabs: number; nodes: number; forwards: number; tunnels: number; activeForwards: number; isAdmin: boolean }) {
  const titles: Record<View, { title: string; description: string; focus: string }> = {
    dashboard: { title: "工作区", description: "汇总查看转发服务、节点与隧道的当前运行状态。", focus: "网络概览" },
    forwards: { title: "转发服务", description: "维护入口端口、目标地址和每条服务的运行状态。", focus: "端口与目标" },
    nodes: { title: "节点管理", description: "查看已注册节点、在线状态和资源使用情况。", focus: "节点健康" },
    tunnels: { title: "隧道编排", description: "组织入口与出口的链路，并管理转发承载。", focus: "链路拓扑" },
    users: { title: "用户管理", description: "管理普通用户账号、流量额度和转发数量。", focus: "账号与额度" },
    xui: { title: "3x-ui 集成", description: "同步 3x-ui 入站，创建转发时快速填充目标地址和端口。", focus: "入站同步" },
    settings: { title: "面板设置", description: "管理控制面连接地址与部署安全参数。", focus: "控制平面" },
  };
  const current = titles[view];
  const moduleCount = nodes + forwards + tunnels + (isAdmin ? 3 : 2);
  return <section className="console-overview"><div className="console-overview-head"><div><span className="overview-chip">OVERVIEW</span><h2>{current.title}</h2><p>{current.description}</p></div><div className="focus-chip"><span>当前焦点</span><strong>{current.focus}</strong></div></div><div className="overview-stats"><div><span>模块</span><strong>{moduleCount}</strong><small>当前控制台可用资源</small></div><div><span>打开标签</span><strong>{tabs}</strong><small>保留在本次工作区</small></div><div><span>已选视图</span><strong>{tabs}/{tabs}</strong><small>{current.title}</small></div><div><span>实时上下文</span><strong>{activeForwards} 条运行</strong><small>{forwards} 转发 · {nodes} 节点 · {tunnels} 隧道</small></div></div></section>;
}

function DashboardView({ nodes, tunnels, forwards, users, onlineNodes, activeForwards, totalUp, totalDown, activity, onNavigate, onCreate, onToggle, onDiagnose, onEdit }: any) {
  const totalTraffic = totalUp + totalDown;
  const rankedForwards = [...forwards].sort((a, b) => trafficUsage(b) - trafficUsage(a)).slice(0, 4);
  const rankedNodes = nodes.map((node: ApiItem) => ({ node, summary: nodeTrafficSummary(Number(node.id), tunnels, forwards) })).sort((a: any, b: any) => b.summary.total - a.summary.total).slice(0, 4);
  return <>
    <section className="dashboard-hero"><div className="hero-copy"><div className="hero-kicker"><span className="live-pulse" /> NETWORK / LIVE</div><h2>你的网络，<em>清晰可见。</em></h2><p>实时掌握节点、隧道与转发服务的状态。需要新增线路时，从这里开始。</p><div className="hero-actions"><button className="button button-primary" onClick={() => onCreate("forwards")}><Plus size={16} /> 新建转发</button><button className="button button-ghost" onClick={() => onNavigate("nodes")}><Router size={15} /> 管理节点</button></div></div><div className="hero-status"><div className="hero-status-top"><span>NETWORK HEALTH</span><strong>{nodes.length ? `${Math.round(onlineNodes / nodes.length * 100)}%` : "--"}</strong></div><div className="health-ring"><div><strong>{onlineNodes}</strong><span>在线节点</span></div></div><div className="hero-status-foot"><span><i className="status-dot is-online" /> {onlineNodes} 个节点在线</span><span>{activeForwards} 条线路运行中</span></div></div></section>
    <section className="dashboard-signal"><div className="signal-main"><div className="section-label"><span>ACTIVE ROUTES</span><button className="text-button" onClick={() => onNavigate("tunnels")}>查看拓扑 <ChevronRight size={14} /></button></div><div className="route-stage"><div className="route-node"><span className="route-icon"><Router size={18} /></span><div><strong>{nodes[0]?.name || "你的入口节点"}</strong><small>{nodes[0]?.serverIp || "等待节点接入"}</small></div><i className="route-state" /></div><div className="route-connector"><span /><span /><span /><span /><span /></div><div className="route-node route-destination"><span className="route-icon"><Network size={18} /></span><div><strong>{forwards[0]?.name || "转发服务"}</strong><small>{forwards[0]?.remoteAddr || "创建第一条线路"}</small></div><i className="route-state" /></div></div><div className="signal-stats"><div><span>运行中转发</span><strong>{activeForwards}<small> / {forwards.length || 0}</small></strong></div><div><span>累计流量</span><strong>{formatBytes(totalUp + totalDown)}</strong></div><div><span>实时连接</span><strong>{nodes.length ? "稳定" : "--"}</strong></div></div></div><div className="signal-aside"><div className="section-label"><span>ACTIVITY</span><button className="text-button" onClick={() => undefined}>全部记录 <ChevronRight size={14} /></button></div><div className="activity-list">{activity.length === 0 ? <div className="activity-empty"><Activity size={18} /><span>你的操作记录会出现在这里</span></div> : activity.slice(0, 3).map((item: any, index: number) => <div className="activity-item" key={`${item.title}-${index}`}><span className={`activity-icon ${item.tone}`}><Check size={13} /></span><div><strong>{item.title}</strong><small>{item.detail}</small></div><time>{item.time}</time></div>)}</div></div></section>
    <section className="traffic-report-grid"><div className="panel traffic-report"><div className="report-heading"><div><span>TRAFFIC REPORT</span><h3>累计流量报表</h3></div><button className="text-button" onClick={() => onNavigate("forwards")}>查看全部转发 <ChevronRight size={14} /></button></div><div className="report-total"><strong>{formatBytes(totalTraffic)}</strong><span>当前已计入的累计上下行流量</span></div><div className="traffic-split"><div><div><span><ArrowUpFromLine size={13} /> 上行</span><strong>{formatBytes(totalUp)}</strong></div><i><b style={{ width: `${totalTraffic ? Math.max(2, totalUp / totalTraffic * 100) : 0}%` }} /></i><small>{totalTraffic ? `${(totalUp / totalTraffic * 100).toFixed(1)}%` : "0%"}</small></div><div><div><span><ArrowDownToLine size={13} /> 下行</span><strong>{formatBytes(totalDown)}</strong></div><i><b className="down" style={{ width: `${totalTraffic ? Math.max(2, totalDown / totalTraffic * 100) : 0}%` }} /></i><small>{totalTraffic ? `${(totalDown / totalTraffic * 100).toFixed(1)}%` : "0%"}</small></div></div><div className="report-foot"><span>统计口径：按转发累计值</span><span>{forwards.length} 条服务 · {tunnels.length} 条链路</span></div></div><div className="panel traffic-ranking"><PanelHeading title="节点流量排名" action="节点详情" onClick={() => onNavigate("nodes")} /><div className="ranking-list">{rankedNodes.length ? rankedNodes.map(({ node, summary }: any, index: number) => <div className="ranking-row" key={node.id}><span className="ranking-index">{String(index + 1).padStart(2, "0")}</span><div><strong>{node.name || `Node-${node.id}`}</strong><small>{summary.forwardCount} 条转发承载</small><i><b style={{ width: `${rankedNodes[0]?.summary.total ? Math.max(3, summary.total / rankedNodes[0].summary.total * 100) : 0}%` }} /></i></div><strong>{formatBytes(summary.total)}</strong></div>) : <EmptyState text="暂无节点流量数据" />}</div></div><div className="panel traffic-ranking"><PanelHeading title="转发流量排名" action="服务列表" onClick={() => onNavigate("forwards")} /><div className="ranking-list">{rankedForwards.length ? rankedForwards.map((forward: ApiItem, index: number) => <div className="ranking-row" key={forward.id}><span className="ranking-index">{String(index + 1).padStart(2, "0")}</span><div><strong>{forward.name || "未命名服务"}</strong><small>{forward.tunnelName || "未关联隧道"}</small><i><b className="pink" style={{ width: `${trafficUsage(rankedForwards[0]) ? Math.max(3, trafficUsage(forward) / trafficUsage(rankedForwards[0]) * 100) : 0}%` }} /></i></div><strong>{formatBytes(trafficUsage(forward))}</strong></div>) : <EmptyState text="暂无转发流量数据" />}</div></div></section>
    <section className="dashboard-grid lower-grid"><div className="panel wide-panel"><PanelHeading title="转发服务" action="管理服务" onClick={() => onNavigate("forwards")} /><div className="table-wrap"><table><thead><tr><th>状态</th><th>服务</th><th>入口</th><th>目标</th><th>流量</th><th>操作</th></tr></thead><tbody>{forwards.length === 0 ? <tr><td colSpan={6}><EmptyState text="还没有转发服务" action="创建第一条线路" onAction={() => onCreate("forwards")} /></td></tr> : forwards.slice(0, 5).map((forward: ApiItem) => <ForwardRow key={forward.id} forward={forward} onToggle={onToggle} onDiagnose={onDiagnose} onEdit={onEdit} />)}</tbody></table></div></div><div className="panel quick-panel"><PanelHeading title="从这里开始" /><button className="quick-action" onClick={() => onCreate("nodes")}><span className="quick-icon teal"><Terminal size={17} /></span><span><strong>接入一个节点</strong><small>获取 Agent 安装命令</small></span><ChevronRight size={16} /></button><button className="quick-action" onClick={() => onCreate("tunnels")}><span className="quick-icon blue"><SlidersHorizontal size={17} /></span><span><strong>组织一条隧道</strong><small>{tunnels.length} 条隧道可用</small></span><ChevronRight size={16} /></button><div className="capacity"><div><span>上行</span><strong>{formatBytes(totalUp)}</strong></div><div className="capacity-bar"><i style={{ width: `${totalUp ? 68 : 4}%` }} /></div><small>{users.length ? `${users.length} 个用户正在使用资源` : "私有部署 · 管理员视图"}</small></div></div></section>
  </>;
}

function PanelHeading({ title, action, onClick }: { title: string; action?: string; onClick?: () => void }) { return <div className="panel-heading"><h3>{title}</h3>{action && <button className="text-button" onClick={onClick}>{action}<ChevronRight size={14} /></button>}</div>; }
function EmptyState({ text, action, onAction }: { text: string; action?: string; onAction?: () => void }) { return <div className="empty-state"><CircleHelp size={18} /><span>{text}</span>{action && <button className="text-button" onClick={onAction}>{action}<ChevronRight size={14} /></button>}</div>; }
function ForwardRow({ forward, onToggle, onDiagnose, onDelete, onEdit }: { forward: ApiItem; onToggle: (item: ApiItem) => void; onDiagnose: (item: ApiItem) => void; onDelete?: (item: ApiItem) => void; onEdit?: (item: ApiItem) => void }) {
  const running = Number(forward.status) === 1;
  const exhausted = Number(forward.status) === 2;
  const entryIp = forward.entryIp || forward.inIp || "0.0.0.0";
  const publicPort = forward.inPort;
  const used = trafficUsage(forward);
  const limit = Number(forward.flow || 0);
  return <tr><td><span className="table-status"><StatusDot status={running} />{running ? "运行中" : exhausted ? "已超额" : "已暂停"}</span></td><td><strong className="table-primary">{forward.name || "未命名服务"}</strong>{forward.xuiInboundName && <small className="table-secondary">3x-ui · {forward.xuiInboundName}</small>}<small className="table-secondary">{forward.tunnelName || "未关联隧道"}</small></td><td className="mono">{entryIp}:{publicPort || "-"}<small className="table-secondary">{Number(forward.tunnelType) === 3 ? "反向隧道公网入口" : "入口"}</small></td><td className="mono target-cell">{forward.remoteAddr || "-"}</td><td><span className="flow-pair"><ArrowUpFromLine size={12} />{formatBytes(Number(forward.inFlow))}<ArrowDownToLine size={12} />{formatBytes(Number(forward.outFlow))}</span><small className="table-secondary">{limit > 0 ? `${formatBytes(used)} / ${limit} GB` : "不限额"}</small></td><td><div className="row-actions"><button className="mini-button" title="编辑转发和流量上限" onClick={() => onEdit?.(forward)}><Settings2 size={14} /></button><button className="mini-button" title="诊断链路" onClick={() => onDiagnose(forward)}><Activity size={14} /></button><button className={`mini-button ${running ? "warning" : "success"}`} title={running ? "暂停服务" : exhausted ? "提高额度后恢复" : "恢复服务"} onClick={() => void onToggle(forward)}>{running ? <Pause size={14} /> : <Play size={14} />}</button>{onDelete && <button className="mini-button danger" title="删除转发" onClick={() => onDelete(forward)}><X size={14} /></button>}</div></td></tr>;
}

function ForwardsView({ forwards, query, setQuery, onCreate, onImport, onToggle, onDiagnose, onDelete, onEdit, onRefresh }: any) {
  const [groupBy, setGroupBy] = useState<"tunnel" | "entry">("tunnel");
  const [sortBy, setSortBy] = useState<"traffic" | "name">("traffic");
  const totalUp = forwards.reduce((sum: number, forward: ApiItem) => sum + Number(forward.inFlow || 0), 0);
  const totalDown = forwards.reduce((sum: number, forward: ApiItem) => sum + (Number(forward.tunnelFlow || 2) === 1 ? 0 : Number(forward.outFlow || 0)), 0);
  const activeCount = forwards.filter((forward: ApiItem) => Number(forward.status) === 1).length;
  const groups = useMemo(() => {
    const buckets = new Map<string, { title: string; detail: string; forwards: ApiItem[] }>();
    forwards.forEach((forward: ApiItem) => {
      const entry = forward.entryIp || forward.inIp || "0.0.0.0";
      const key = groupBy === "tunnel" ? `tunnel-${forward.tunnelId || "none"}` : `entry-${entry}`;
      const title = groupBy === "tunnel" ? forward.tunnelName || "未关联隧道" : `入口节点 ${entry}`;
      const detail = groupBy === "tunnel" ? `入口 ${entry}` : forward.tunnelName || "未关联隧道";
      const bucket: { title: string; detail: string; forwards: ApiItem[] } = buckets.get(key) || { title, detail, forwards: [] };
      bucket.forwards.push(forward);
      buckets.set(key, bucket);
    });
    return [...buckets.values()]
      .map((group) => ({ ...group, forwards: [...group.forwards].sort((a, b) => sortBy === "traffic" ? trafficUsage(b) - trafficUsage(a) : String(a.name || "").localeCompare(String(b.name || ""), "zh-CN")) }))
      .sort((a, b) => b.forwards.reduce((sum, forward) => sum + trafficUsage(forward), 0) - a.forwards.reduce((sum, forward) => sum + trafficUsage(forward), 0));
  }, [forwards, groupBy, sortBy]);

  return <>
    <section className="page-intro"><div><p className="eyebrow">SERVICE REGISTRY / 02</p><h2>转发服务</h2><p className="muted">按链路或入口分组查看每条转发的累计上下行流量。</p></div><div className="intro-actions"><button className="button button-quiet" onClick={onImport}><ArrowUpFromLine size={15} /> 导入转发</button><button className="button button-primary" onClick={onCreate}><Plus size={16} /> 新建转发</button></div></section>
    <section className="traffic-overview" aria-label="转发流量汇总"><div><span>累计流量</span><strong>{formatBytes(totalUp + totalDown)}</strong><small>{forwards.length} 条转发</small></div><div><span><ArrowUpFromLine size={13} /> 上行</span><strong>{formatBytes(totalUp)}</strong><small>入口接收</small></div><div><span><ArrowDownToLine size={13} /> 下行</span><strong>{formatBytes(totalDown)}</strong><small>目标发送</small></div><div><span>运行中</span><strong>{activeCount}<small> / {forwards.length}</small></strong><small>服务状态</small></div></section>
    <section className="panel table-panel"><div className="table-toolbar traffic-toolbar"><div className="search-box"><Search size={16} /><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="搜索服务、隧道或目标地址" /></div><div className="traffic-controls"><div className="segmented-control" aria-label="流量分组方式"><button className={groupBy === "tunnel" ? "active" : ""} onClick={() => setGroupBy("tunnel")}>按隧道</button><button className={groupBy === "entry" ? "active" : ""} onClick={() => setGroupBy("entry")}>按入口</button></div><div className="segmented-control" aria-label="组内排序方式"><button className={sortBy === "traffic" ? "active" : ""} onClick={() => setSortBy("traffic")}>流量优先</button><button className={sortBy === "name" ? "active" : ""} onClick={() => setSortBy("name")}>名称排序</button></div><button className="button button-quiet" onClick={onRefresh}><RefreshCw size={15} /> 刷新列表</button></div></div><div className="table-wrap"><table><thead><tr><th>状态</th><th>服务</th><th>入口</th><th>目标地址</th><th>累计流量</th><th>维护</th></tr></thead><tbody>{groups.length ? groups.map((group) => { const groupUp = group.forwards.reduce((sum, forward) => sum + Number(forward.inFlow || 0), 0); const groupDown = group.forwards.reduce((sum, forward) => sum + (Number(forward.tunnelFlow || 2) === 1 ? 0 : Number(forward.outFlow || 0)), 0); return <Fragment key={`${groupBy}-${group.title}`}><tr className="traffic-group-row"><td colSpan={6}><div className="traffic-group-head"><div><strong>{group.title}</strong><span>{group.detail} · {group.forwards.length} 条转发</span></div><div className="traffic-group-total"><span><ArrowUpFromLine size={11} /> {formatBytes(groupUp)}</span><span><ArrowDownToLine size={11} /> {formatBytes(groupDown)}</span><strong>{formatBytes(groupUp + groupDown)}</strong></div></div></td></tr>{group.forwards.map((forward) => <ForwardRow key={forward.id} forward={forward} onToggle={onToggle} onDiagnose={onDiagnose} onDelete={onDelete} onEdit={onEdit} />)}</Fragment>; }) : <tr><td colSpan={6}><EmptyState text="没有匹配的转发服务" /></td></tr>}</tbody></table></div></section>
  </>;
}

function ImportForwardModal({ tunnels, onClose, onSubmit }: { tunnels: ApiItem[]; onClose: () => void; onSubmit: (tunnelId: number, rows: ImportForwardRow[]) => Promise<ImportForwardOutcome[]> }) {
  const [tunnelId, setTunnelId] = useState(String(tunnels[0]?.id || ""));
  const [text, setText] = useState("");
  const [outcomes, setOutcomes] = useState<ImportForwardOutcome[]>([]);
  const [parseErrors, setParseErrors] = useState<ImportForwardOutcome[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const selectedTunnel = tunnels.find((tunnel) => String(tunnel.id) === tunnelId);
  const reverseRelay = Number(selectedTunnel?.type) === 3;
  const parsed = parseForwardImport(text, reverseRelay);
  const results = [...parseErrors, ...outcomes].sort((a, b) => a.line - b.line);
  const successCount = outcomes.filter((outcome) => outcome.ok).length;
  const retryableCount = outcomes.filter((outcome) => !outcome.ok).length;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!tunnelId) return;
    if (parsed.rows.length === 0) {
      setParseErrors(parsed.errors.length ? parsed.errors : [{ line: 0, raw: "", ok: false, message: "请先粘贴至少一行有效数据" }]);
      setOutcomes([]);
      return;
    }
    const retrying = outcomes.length > 0;
    const rowsToSubmit = retrying
      ? parsed.rows.filter((row) => outcomes.some((outcome) => outcome.line === row.line && !outcome.ok))
      : parsed.rows;
    if (rowsToSubmit.length === 0) {
      toast.success("没有失败的转发需要重试");
      return;
    }
    setSubmitting(true);
    setParseErrors(parsed.errors);
    try {
      const retryOutcomes = await onSubmit(Number(tunnelId), rowsToSubmit);
      if (!retrying) {
        setOutcomes(retryOutcomes);
      } else {
        const retryByLine = new Map(retryOutcomes.map((outcome) => [outcome.line, outcome]));
        setOutcomes(outcomes.map((outcome) => outcome.ok ? outcome : retryByLine.get(outcome.line) || outcome));
      }
    } finally {
      setSubmitting(false);
    }
  };

  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget && !submitting) onClose(); }}><section className="create-modal import-modal" role="dialog" aria-modal="true" aria-label="导入转发">
    <div className="modal-head"><div><span className="eyebrow">BULK IMPORT / FORWARDS</span><h3>{reverseRelay ? "批量端口映射" : "导入转发"}</h3><p className="modal-subtitle">格式：{reverseRelay ? "目标地址|映射名称|公网入口端口" : "目标地址|转发名称|入口端口"}</p></div><button className="icon-button" type="button" onClick={onClose} disabled={submitting} title="关闭"><X size={17} /></button></div>
    <form onSubmit={submit} className="modal-form">
      <label className="modal-field">选择隧道<select required value={tunnelId} onChange={(event) => { setTunnelId(event.target.value); setOutcomes([]); setParseErrors([]); }} disabled={submitting || tunnels.length === 0}><option value="" disabled>请选择隧道</option>{tunnels.map((tunnel) => <option value={tunnel.id} key={tunnel.id}>{tunnel.name || `Tunnel-${tunnel.id}`}</option>)}</select></label>
      <label className="modal-field">转发数据<textarea className="import-textarea" value={text} onChange={(event) => { setText(event.target.value); setOutcomes([]); setParseErrors([]); }} disabled={submitting} placeholder={reverseRelay ? '127.0.0.1:3389|Windows-RDP|13389\n127.0.0.1:445|Windows-SMB|14445\n\n公网入口端口留空时会自动分配' : '192.168.100.112:22|转发ssh|50003\n192.168.100.1:80|kuai|50002\n\n入口端口留空时会自动分配'} spellCheck={false} /></label>
      <div className="import-help"><span>{reverseRelay ? "每行一条：目标服务与公网入口端口；反向控制端口由系统自动隔离分配。" : "每行一条，目标地址必须包含目标端口。"}</span><span>IPv6 请使用 [2001:db8::5]:443。</span></div>
      {results.length > 0 && <div className="import-results"><div className="import-results-head"><span>导入结果</span><strong>{successCount} 条成功{parsed.errors.length + outcomes.filter((outcome) => !outcome.ok).length > 0 ? ` · ${parsed.errors.length + outcomes.filter((outcome) => !outcome.ok).length} 条失败` : ""}</strong></div>{results.map((result, index) => <div className={`import-result ${result.ok ? "success" : "failure"}`} key={`${result.line}-${index}`}><span>{result.ok ? <Check size={13} /> : <X size={13} />}</span><div><strong>第 {result.line || "-"} 行</strong><small>{result.message}{result.raw && ` · ${result.raw}`}</small></div></div>)}</div>}
      {tunnels.length === 0 && <div className="import-empty">当前没有可用隧道，请先创建或授权一个隧道。</div>}
      <div className="modal-actions"><button className="button button-quiet" type="button" onClick={onClose} disabled={submitting}>关闭</button><button className="button button-primary" type="submit" disabled={submitting || tunnels.length === 0 || (outcomes.length > 0 && retryableCount === 0)}>{submitting ? "导入中..." : outcomes.length > 0 ? "重试失败项" : "开始导入"}</button></div>
    </form>
  </section></div>;
}
function NodesView({ nodes, tunnels, forwards, nodeStats, onCreate, onEdit, onRefresh, onInstall, onReload }: any) {
  const summaries = nodes.map((node: ApiItem) => ({ node, summary: nodeTrafficSummary(Number(node.id), tunnels, forwards) }));
  const highestTraffic = Math.max(...summaries.map((item: any) => item.summary.total), 0);
  return <><section className="page-intro"><div><p className="eyebrow">AGENT FLEET / 03</p><h2>节点管理</h2><p className="muted">每个节点展示当前承载的转发累计流量，入口与出口分别统计。</p></div><div className="intro-actions"><button className="button button-quiet" onClick={onReload}><RefreshCw size={15} /> 同步状态</button><button className="button button-primary" onClick={onCreate}><Terminal size={15} /> 接入节点</button></div></section><section className="node-grid">{nodes.length ? summaries.map(({ node, summary }: any) => <div className="panel node-card" key={node.id}><div className="node-card-top"><span className="node-symbol large"><HardDrive size={20} /></span><div><h3>{node.name || `Node-${node.id}`}</h3><p>{node.serverIp || node.ip || "未配置地址"}</p>{reverseNodeRoles(Number(node.id), tunnels).length > 0 && <small className="table-secondary">{reverseNodeRoles(Number(node.id), tunnels).join(" · ")}</small>}</div><span className={`node-status ${Number(node.status) === 1 ? "online" : "offline"}`}><StatusDot status={Number(node.status) === 1} />{Number(node.status) === 1 ? "在线" : "离线"}</span></div><div className="node-card-meta"><div><span>VERSION</span><strong>{node.version || "--"}</strong></div><div><span>PORT POOL</span><strong>{node.portRange || "手动指定"}</strong></div><div><span>LAST UPDATE</span><strong>{formatDate(node.updatedTime)}</strong></div></div><div className="node-traffic"><div className="node-traffic-head"><span>节点累计流量</span><strong>{formatBytes(summary.total)}</strong></div><div className="node-traffic-values"><span><ArrowUpFromLine size={12} /> 入口承载 <strong>{formatBytes(summary.ingress)}</strong></span><span><ArrowDownToLine size={12} /> 出口承载 <strong>{formatBytes(summary.egress)}</strong></span></div><div className="node-traffic-bar"><i style={{ width: `${highestTraffic ? Math.max(3, summary.total / highestTraffic * 100) : 0}%` }} /></div><small>{summary.forwardCount} 条关联转发 · 当前节点最大承载的 {highestTraffic ? `${(summary.total / highestTraffic * 100).toFixed(0)}%` : "0%"}</small></div><div className="resource-bars"><ResourceBar label="CPU" value={nodeStats[String(node.id)]?.cpu || 0} /><ResourceBar label="MEMORY" value={nodeStats[String(node.id)]?.memory || 0} /></div><div className="node-card-actions"><button className="button button-quiet" onClick={() => onEdit(node)}><Settings2 size={14} /> 编辑节点</button><button className="button button-quiet" onClick={() => onRefresh(node)}><RefreshCw size={14} /> 刷新心跳</button><button className="button button-quiet" onClick={() => void onInstall(node)}><Terminal size={14} /> Linux 命令</button><button className="button button-quiet" onClick={() => void onInstall(node, "windows")}><Terminal size={14} /> Windows 命令</button></div></div>) : <div className="panel empty-large"><Router size={24} /><h3>还没有接入节点</h3><p>创建节点后，在服务器执行 Agent 安装命令即可开始上报。</p><button className="button button-primary" onClick={onCreate}><Plus size={16} /> 创建第一个节点</button></div>}</section></>;
}
function ResourceBar({ label, value }: { label: string; value: number }) { return <div className="resource-bar"><div><span>{label}</span><strong>{value ? `${value.toFixed(0)}%` : "--"}</strong></div><div className="bar-track"><i style={{ width: `${Math.min(100, Math.max(value ? 3 : 0, value))}%` }} /></div></div>; }
function TunnelsView({ tunnels, forwards, nodes, onCreate, onManage, onDiagnose, onDelete }: any) { return <><section className="page-intro"><div><p className="eyebrow">ROUTE TOPOLOGY / 04</p><h2>隧道编排</h2><p className="muted">管理入口到出口的链路、流量倍率和运行参数。</p></div><button className="button button-primary" onClick={onCreate}><Plus size={16} /> 创建隧道</button></section><section className="tunnel-list">{tunnels.length ? tunnels.map((tunnel: ApiItem) => { const inNode = nodes.find((node: ApiItem) => node.id === tunnel.inNodeId); const outNode = nodes.find((node: ApiItem) => node.id === tunnel.outNodeId); const forwardCount = forwards.filter((forward: ApiItem) => Number(forward.tunnelId) === Number(tunnel.id)).length; const flowType = Number(tunnel.flowType || tunnel.flow || 2); const reverseRelay = Number(tunnel.type) === 3; const used = flowType === 1 ? Number(tunnel.inFlow || 0) : Number(tunnel.inFlow || 0) + Number(tunnel.outFlow || 0); const limit = Number(tunnel.flowLimitGb || 0); return <div className="panel tunnel-card" key={tunnel.id}><div className="tunnel-title"><span className="tunnel-id">T{String(tunnel.id).padStart(2, "0")}</span><div><h3>{tunnel.name || "未命名隧道"}</h3><p>{tunnel.protocol || "tcp"} · {forwardCount} 条转发承载</p></div><span className={`node-status ${Number(tunnel.status) === 1 ? "online" : "offline"}`}><StatusDot status={Number(tunnel.status) === 1} />{Number(tunnel.status) === 1 ? "可用" : "停用"}</span></div><div className="tunnel-path"><div><span>{reverseRelay ? "公网入口节点" : "入口节点"}</span><strong>{inNode?.name || tunnel.inIp || `Node-${tunnel.inNodeId}`}</strong><small>{tunnel.inIp || "-"}</small></div><div className="path-line"><i /><i /><i /><ChevronRight size={18} /></div><div><span>{reverseRelay ? "内网 Windows 节点" : "出口节点"}</span><strong>{outNode?.name || tunnel.outIp || `Node-${tunnel.outNodeId}`}</strong><small>{tunnel.outIp || "-"}</small></div></div><div className="tunnel-footer"><span><Gauge size={14} /> 流量 {formatBytes(used)} / {limit > 0 ? `${limit} GB` : "不限"}</span><span><Gauge size={14} /> {flowType === 1 ? "单向记录" : "双向记录"}</span><span><Gauge size={14} /> 统计倍率 {tunnel.trafficRatio || 1} 倍</span><span><Network size={14} /> {reverseRelay ? "内网反向中继" : tunnel.type === 2 ? "中继链路" : "直连出口"}</span><span><Gauge size={14} /> 限速 {Number(tunnel.speedLimitKbps) > 0 ? `${tunnel.speedLimitKbps} KB/s` : "不限"}</span><button className="text-button" onClick={() => onDiagnose(tunnel)}><Activity size={14} /> 诊断</button><button className="text-button" onClick={() => onManage(tunnel)}><Settings2 size={14} /> 管理</button><button className="text-button danger-text" onClick={() => onDelete(tunnel)}><X size={14} /> 删除</button></div></div>; }) : <div className="panel empty-large"><SlidersHorizontal size={24} /><h3>还没有隧道</h3><p>隧道用于组织节点之间的转发路径。</p><button className="button button-primary" onClick={onCreate}><Plus size={16} /> 创建隧道</button></div>}</section></>; }
function DiagnosticDrawer({ diagnostic, onClose }: { diagnostic: DiagnosticState; onClose: () => void }) {
  const targets = diagnosticTargets(diagnostic);
  const passed = targets.filter((target) => target.success).length;
  const resourceName = diagnostic.resource.name || `${diagnostic.kind === "forward" ? "转发" : "隧道"}-${diagnostic.resource.id}`;
  const running = diagnostic.status === "running";
  const failed = diagnostic.status === "error" || (diagnostic.status === "success" && (targets.length === 0 || passed < targets.length));
  const statusLabel = running ? "正在诊断" : failed ? "发现问题" : "诊断通过";
  const statusTone = running ? "running" : failed ? "failed" : "passed";
  const tunnelData = diagnostic.kind === "tunnel" ? asRecord(diagnostic.response?.data) : {};
  const forwardData = diagnostic.kind === "forward" ? asRecord(diagnostic.response?.data) : {};
  const forwardProbeRole = forwardData.probeNodeRole === "windows" ? "内网 Windows 节点" : forwardData.probeNodeRole === "exit" ? "出口节点" : "入口节点";
  const compatibilityFallback = diagnostic.kind === "tunnel" && tunnelData.probeMode === "tcp-compat";
  const stagedTunnelDiagnostic = diagnostic.kind === "tunnel" && Array.isArray(tunnelData.results);
  const isHttpResult = diagnostic.kind === "tunnel" && !compatibilityFallback && !stagedTunnelDiagnostic;
  const describeTarget = (target: DiagnosticTarget) => {
    if (stagedTunnelDiagnostic) return target.error || (target.success ? "检查通过" : "检查失败");
    if (compatibilityFallback) return target.success ? "TCP 443 连接成功（旧版 Agent）" : target.error || "TCP 443 连接失败（旧版 Agent）";
    if (!isHttpResult) return target.success ? "TCP 连接成功" : target.error || "TCP 连接失败";
    if (target.success) return target.redirected ? `HTTP ${target.statusCode}，网站可访问，已跳转` : `HTTP ${target.statusCode}，网站可访问`;
    if (target.reachable) return target.error || `网站已响应，但 HTTP 状态为 ${target.statusCode || "异常"}`;
    const labels: Record<string, string> = { dns: "DNS 解析失败", tls: "TLS 握手失败", timeout: "连接超时", connection: "无法建立连接", invalid_url: "探测地址无效" };
    return labels[target.errorType || ""] || target.error || "网站未响应";
  };
  const summaryText = running
    ? isHttpResult ? "请求已发出，正在等待入口节点完成 HTTP/HTTPS 探测。" : "请求已发出，正在等待入口节点返回 TCP 探测结果。"
    : diagnostic.error || (compatibilityFallback ? (failed ? "当前 Agent 不支持 HTTP 网站探测，且 Cloudflare TCP 443 连接失败。" : "当前 Agent 不支持 HTTP 网站探测，已回退为 Cloudflare TCP 443 连接测试。升级 Agent 后可查看 HTTP 状态码。") : (failed ? (isHttpResult ? "网站未能正常访问，请查看 HTTP 状态或网络错误明细。" : "至少一个探测目标不可达，请查看下方明细。") : (isHttpResult ? "入口节点已完成 HTTP/HTTPS 探测，链路结果如下。" : "入口节点已完成探测，链路结果如下。")));

  return <aside className="drawer diagnostic-drawer" role="dialog" aria-modal="true" aria-label="诊断结果">
    <div className="drawer-head">
      <div><span className="eyebrow">DIAGNOSTIC / {diagnostic.kind === "forward" ? "FORWARD" : "TUNNEL"}</span><h3>{resourceName}</h3></div>
      <button className="icon-button" onClick={onClose} title="关闭诊断面板"><X size={17} /></button>
    </div>
    <div className={`diagnose-summary ${statusTone}`} aria-live="polite">
      <span className="result-icon">{running ? <Activity size={20} className="spin" /> : failed ? <X size={20} /> : <Check size={20} />}</span>
      <div><strong>{statusLabel}</strong><p>{summaryText}</p></div>
    </div>
    <div className="diagnose-progress" aria-label={running ? "诊断进度" : "诊断已完成"}><i className={running ? "is-running" : "is-complete"} /></div>
    <div className="diagnose-steps"><span className="done"><Check size={12} />已提交</span><span className={running ? "active" : "done"}>{running ? <Activity size={12} className="spin" /> : <Check size={12} />}探测节点</span><span className={!running ? "done" : ""}>{!running && <Check size={12} />}读取结果</span></div>
    <div className="drawer-section"><span>{diagnostic.kind === "forward" && Number(diagnostic.resource.tunnelType) === 3 ? "公网入口" : diagnostic.kind === "forward" ? "转发入口" : "入口节点"}</span><strong className="mono">{tunnelData.entryNodeAddress || diagnostic.resource.entryIp || diagnostic.resource.inIp || "-"}{diagnostic.kind === "forward" && diagnostic.resource.inPort ? `:${diagnostic.resource.inPort}` : ""}</strong></div>
    {diagnostic.kind === "forward" && <div className="drawer-section"><span>目标探测节点</span><strong>{forwardProbeRole}</strong><small>{forwardData.probeNodeRole === "windows" ? "反向隧道由内网 Windows 节点连接目标地址。" : "中继隧道会由出口节点连接目标地址。"}</small></div>}
    {diagnostic.kind === "tunnel" && stagedTunnelDiagnostic && <div className="drawer-section"><span>出口节点</span><strong className="mono">{tunnelData.exitNode?.address || diagnostic.resource.outIp || "-"}</strong><small>依次检查入口、出口、入口到出口链路和出口到 Cloudflare。</small></div>}
    {!stagedTunnelDiagnostic && <div className="drawer-section"><span>{isHttpResult ? "Cloudflare 测试网站" : compatibilityFallback ? "Cloudflare TCP 兼容测试" : "目标地址"}</span><strong className="mono">{isHttpResult || compatibilityFallback ? (tunnelData.targetUrl || targets[0]?.target || "-") : (diagnostic.resource.remoteAddr || `${diagnostic.resource.outIp || "出口节点"}:443`)}</strong></div>}
    {running && <div className="diagnose-wait"><Activity size={15} className="spin" /><span>节点响应通常需要几秒，请保持此面板打开。</span></div>}
    {!running && <div className="diagnose-results"><div className="diagnose-results-head"><span>探测明细</span><strong>{passed}/{targets.length || 0} {isHttpResult ? "个网站可访问" : "个目标通过"}</strong></div>{targets.length === 0 ? <div className="diagnose-empty">服务未返回可解析的目标结果。</div> : targets.map((target, index) => <div className="diagnose-target" key={`${target.target}-${index}`}><div className={`target-state ${target.success ? "success" : "failure"}`}>{target.success ? <Check size={14} /> : <X size={14} />}</div><div className="target-copy"><strong className="mono">{target.label || target.target}</strong>{target.label && target.label !== target.target && <small className="mono">{target.target}</small>}<small>{describeTarget(target)}</small>{isHttpResult && target.finalUrl && target.finalUrl !== target.target && <small>最终地址：{target.finalUrl}</small>}</div><div className="target-metrics">{target.statusCode !== undefined && <span>HTTP {target.statusCode}</span>}{target.duration !== undefined && <span>{target.duration.toFixed(1)} ms</span>}{target.averageTime !== undefined && <span>{target.averageTime.toFixed(1)} ms</span>}{target.packetLoss !== undefined && <span>丢包 {target.packetLoss.toFixed(0)}%</span>}</div></div>)}</div>}
    <button className="button button-primary button-wide" onClick={onClose}>{running ? "后台运行并关闭" : "完成"}</button>
  </aside>;
}

function CreateModal({ kind, nodes, tunnels, xuiInbounds, onClose, onSubmit }: { kind: Exclude<Creator, null>; nodes: ApiItem[]; tunnels: ApiItem[]; xuiInbounds: ApiItem[]; onClose: () => void; onSubmit: (kind: Exclude<Creator, null>, values: Record<string, string>) => Promise<boolean> }) {
  const [values, setValues] = useState<Record<string, string>>(
    kind === "nodes" ? { name: "", serverIp: "", ip: "", portRange: "" } :
      kind === "tunnels" ? { name: "", inNodeId: String(nodes[0]?.id || ""), outNodeId: String(nodes[1]?.id || nodes[0]?.id || ""), type: "2", protocol: "tls", flowType: "2", flowLimitGb: "0", trafficRatio: "1", speedLimitKbps: "0" } :
        { name: "", tunnelId: String(tunnels[0]?.id || ""), xuiInboundId: "", remoteAddr: "", inPort: "", outPort: "", autoAssignPort: "false", flow: "0", strategy: "fifo" },
  );
  const title = kind === "nodes" ? "接入节点" : kind === "tunnels" ? "创建隧道" : "新建转发";
  const update = (key: string, value: string) => setValues((current) => {
    const next = { ...current, [key]: value };
    if (kind === "tunnels" && key === "type" && value === "1") {
      next.protocol = "tls";
      next.anyTlsPassword = "";
    }
    if (kind === "forwards" && key === "xuiInboundId") {
      const inbound = xuiInbounds.find((item) => String(item.id) === value);
      if (inbound) {
        next.name = inbound.name || next.name;
        next.remoteAddr = inbound.remoteAddr || next.remoteAddr;
      }
    }
    return next;
  });
  const generateAnyTlsPassword = () => {
    const bytes = new Uint8Array(18);
    crypto.getRandomValues(bytes);
    update("anyTlsPassword", Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join(""));
  };
  const submit = async (event: FormEvent) => { event.preventDefault(); await onSubmit(kind, values); };
  const field = (key: string, label: string, placeholder: string, type = "text", required = true) => <label className="modal-field">{label}<input required={required} value={values[key] || ""} onChange={(event) => update(key, event.target.value)} type={type} placeholder={placeholder} /></label>;
  const tunnelProtocol = kind === "tunnels" && (values.type === "2" || values.type === "3");
  const automaticallyAssignPort = values.autoAssignPort === "true";
  const selectedForwardTunnel = kind === "forwards" ? tunnels.find((tunnel) => String(tunnel.id) === values.tunnelId) : undefined;
  const requiresRelayPort = Number(selectedForwardTunnel?.type) === 2 || Number(selectedForwardTunnel?.type) === 3;
  const reverseRelay = Number(selectedForwardTunnel?.type) === 3;
  const reverseTunnelSetup = kind === "tunnels" && values.type === "3";
  const localPortLabel = reverseRelay ? "公网映射端口" : "入口端口";
  const relayPortLabel = "中继内部端口";
  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><section className="create-modal" role="dialog" aria-modal="true"><div className="modal-head"><div><span className="eyebrow">NEW CONFIGURATION</span><h3>{title}</h3></div><button className="icon-button" type="button" onClick={onClose} title="关闭"><X size={17} /></button></div><form onSubmit={submit} className="modal-form">
    {kind === "nodes" && <>{field("name", "节点名称", "例如：HK Relay 01")}{field("serverIp", "节点公网地址（可选）", "例如：203.0.113.10", "text", false)}<small className="field-help">节点需要对外提供转发入口时填写。无公网节点可留空，并作为反向中继的出口节点。</small>{field("ip", "节点内网地址", "可选，例如：192.168.1.10", "text", false)}{field("portRange", "自动分配端口池（可选）", "例如：50000-65535,5000", "text", false)}<small className="field-help">手动创建转发时无需填写。启用“自动分配”或使用中继隧道时需要设置；支持单个端口、连续范围和逗号组合。</small></>}
    {kind === "tunnels" && <>{field("name", "隧道名称", "例如：HK → Tokyo")}{<div className="modal-columns"><label className="modal-field">{reverseTunnelSetup ? "公网入口节点" : "入口节点"}<select required value={values.inNodeId} onChange={(event) => update("inNodeId", event.target.value)}>{nodes.map((node) => <option value={node.id} key={node.id}>{node.name || `Node-${node.id}`}</option>)}</select></label><label className="modal-field">{reverseTunnelSetup ? "内网 Windows 节点" : "出口节点"}<select required value={values.outNodeId} onChange={(event) => update("outNodeId", event.target.value)}>{nodes.map((node) => <option value={node.id} key={node.id}>{node.name || `Node-${node.id}`}</option>)}</select></label></div>}<div className="modal-columns"><label className="modal-field">类型<select value={values.type} onChange={(event) => update("type", event.target.value)}><option value="1">直连出口</option><option value="2">中继隧道</option><option value="3">内网反向中继</option></select><small className="field-help">{reverseTunnelSetup ? "公网入口节点负责监听映射端口；内网 Windows 节点仅向外主动连接，无需公网 IP。" : "入口节点必须公网可达；出口节点可在内网，只需主动连接入口节点。"}</small></label><label className="modal-field">协议<select value={values.protocol} onChange={(event) => update("protocol", event.target.value)} disabled={!tunnelProtocol}><option value="tls">TLS</option><option value="tcp">TCP</option>{tunnelProtocol && <option value="anytls">AnyTLS</option>}</select></label></div><div className="modal-columns"><label className="modal-field">流量记录类型<select value={values.flowType} onChange={(event) => update("flowType", event.target.value)}><option value="1">单向记录（仅入口到目标）</option><option value="2">双向记录（入口与返回合计）</option></select></label>{field("flowLimitGb", "隧道流量上限（GB，0不限）", "0", "number")}</div><div className="modal-columns">{field("trafficRatio", "流量统计倍率", "1", "number")}{field("speedLimitKbps", "转发限速（KB/s，0不限）", "0", "number")}</div>{values.protocol === "anytls" && <label className="modal-field">AnyTLS 密码<div className="modal-input-action"><input required value={values.anyTlsPassword || ""} onChange={(event) => update("anyTlsPassword", event.target.value)} type="password" placeholder="请输入中继加密密码" /><button className="mini-button" type="button" title="生成随机密码" onClick={generateAnyTlsPassword}><RefreshCw size={14} /></button></div></label>}</>}
    {kind === "forwards" && <>{field("name", "服务名称", "例如：Private API")}{<label className="modal-field">关联隧道<select required value={values.tunnelId} onChange={(event) => update("tunnelId", event.target.value)}>{tunnels.map((tunnel) => <option value={tunnel.id} key={tunnel.id}>{tunnel.name || `Tunnel-${tunnel.id}`}</option>)}</select></label>}{xuiInbounds.length > 0 && <label className="modal-field">3x-ui 入站（可选）<select value={values.xuiInboundId || ""} onChange={(event) => update("xuiInboundId", event.target.value)}><option value="">手动填写目标地址</option>{xuiInbounds.filter((inbound) => inbound.enabled).map((inbound) => <option value={inbound.id} key={inbound.id}>{inbound.connectionName} · {inbound.name} · {inbound.remoteAddr}</option>)}</select><small className="field-help">选择后自动使用同步到的入站地址和端口。</small></label>}<label className="modal-field">目标地址<input required value={values.remoteAddr || ""} onChange={(event) => update("remoteAddr", event.target.value)} placeholder="IPv4：10.0.0.5:443；IPv6：[2001:db8::5]:443" /><small className="field-help">选择 3x-ui 入站后会自动填充，也可以手动填写多个目标。</small></label><label className="modal-field">选择策略<select value={values.strategy} onChange={(event) => update("strategy", event.target.value)}><option value="fifo">故障切换：优先第一个目标</option><option value="round">轮询：依次分配目标</option><option value="random">随机：随机选择目标</option><option value="hash">固定来源：同一来源尽量保持同一目标</option></select></label><div className="modal-columns">{automaticallyAssignPort ? <div className="modal-field"><span>{localPortLabel}</span><strong className="form-readonly">自动分配</strong></div> : field("inPort", localPortLabel, "例如：50001", "number")}{field("flow", "流量上限（GB，0不限）", "0", "number")}</div>{requiresRelayPort && !reverseRelay && <label className="modal-field">{relayPortLabel}（可选）<input value={values.outPort || ""} onChange={(event) => update("outPort", event.target.value)} type="number" min="1" max="65535" placeholder="例如：50001" /><small className="field-help">双节点中继留空时会复用手动填写的入口端口。</small></label>}<label className="setting-toggle"><input type="checkbox" checked={automaticallyAssignPort} onChange={(event) => { update("autoAssignPort", String(event.target.checked)); if (event.target.checked) update("inPort", ""); }} /><span><strong>自动分配{reverseRelay ? "公网映射" : "入口"}端口</strong><small>从对应节点的自动分配端口池选择未占用端口。</small></span></label></>}
    <div className="modal-actions"><button className="button button-quiet" type="button" onClick={onClose}>取消</button><button className="button button-primary" type="submit"><Check size={15} /> 创建配置</button></div>
  </form></section></div>;
}

function NodeEditModal({ node, onClose, onSubmit }: { node: ApiItem; onClose: () => void; onSubmit: (values: Record<string, string>) => Promise<boolean> }) {
  const [values, setValues] = useState<Record<string, string>>({
    name: node.name || "",
    serverIp: node.serverIp || "",
    ip: node.ip || "",
    portRange: node.portRange || (node.portSta && node.portEnd ? `${node.portSta}-${node.portEnd}` : ""),
  });
  const update = (key: string, value: string) => setValues((current) => ({ ...current, [key]: value }));
  const submit = async (event: FormEvent) => { event.preventDefault(); await onSubmit(values); };

  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><section className="create-modal" role="dialog" aria-modal="true"><div className="modal-head"><div><span className="eyebrow">NODE SETTINGS</span><h3>编辑节点 · {node.name || `Node-${node.id}`}</h3></div><button className="icon-button" type="button" onClick={onClose} title="关闭"><X size={17} /></button></div><form onSubmit={submit} className="modal-form"><label className="modal-field">节点名称<input required value={values.name} onChange={(event) => update("name", event.target.value)} placeholder="例如：HK Relay 01" /></label><label className="modal-field">节点公网地址（可选）<input value={values.serverIp} onChange={(event) => update("serverIp", event.target.value)} placeholder="例如：node.example.com" /><small className="field-help">无公网节点可留空，并作为反向中继的出口节点。</small></label><label className="modal-field">节点内网地址<input value={values.ip} onChange={(event) => update("ip", event.target.value)} placeholder="可选，例如：10.0.0.5" /></label><label className="modal-field">自动分配端口池（可选）<input value={values.portRange} onChange={(event) => update("portRange", event.target.value)} placeholder="例如：50000-65535,5000" /><small className="field-help">手动指定转发端口时无需设置。启用自动分配或使用中继隧道时需要设置。</small></label><div className="modal-actions"><button className="button button-quiet" type="button" onClick={onClose}>取消</button><button className="button button-primary" type="submit"><Check size={15} /> 保存节点</button></div></form></section></div>;
}

function ForwardEditModal({ forward, onClose, onSubmit, onDiagnose }: { forward: ApiItem; onClose: () => void; onSubmit: (values: Record<string, string>) => Promise<boolean>; onDiagnose: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({ name: forward.name || "", remoteAddr: forward.remoteAddr || "", inPort: String(forward.inPort || ""), strategy: forward.strategy || "fifo", interfaceName: forward.interfaceName || "", flow: String(forward.flow || 0) });
  const update = (key: string, value: string) => setValues((current) => ({ ...current, [key]: value }));
  const submit = async (event: FormEvent) => { event.preventDefault(); await onSubmit(values); };
  const used = trafficUsage(forward);
  const reverseRelay = Number(forward.tunnelType) === 3;
  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><section className="create-modal" role="dialog" aria-modal="true"><div className="modal-head"><div><span className="eyebrow">FORWARD SETTINGS</span><h3>管理转发 · {forward.name || `Forward-${forward.id}`}</h3></div><button className="icon-button" type="button" onClick={onClose} title="关闭"><X size={17} /></button></div><form onSubmit={submit} className="modal-form"><label className="modal-field">服务名称<input required value={values.name} onChange={(event) => update("name", event.target.value)} /></label><div className="modal-columns"><label className="modal-field">{reverseRelay ? "公网映射端口" : "入口端口"}<input required type="number" min="1" max="65535" value={values.inPort} onChange={(event) => update("inPort", event.target.value)} /><small className="field-help">端口被占用时可在此切换。保存后会同步更新节点配置。</small></label><div className="modal-field"><span>{reverseRelay ? "公网入口地址" : "入口地址"}</span><strong className="form-readonly mono">{forward.entryIp || forward.inIp || "-"}:{values.inPort || "-"}</strong></div></div><label className="modal-field">目标地址<input required value={values.remoteAddr} onChange={(event) => update("remoteAddr", event.target.value)} /></label><label className="modal-field">选择策略<select value={values.strategy} onChange={(event) => update("strategy", event.target.value)}><option value="fifo">故障切换</option><option value="round">轮询</option><option value="random">随机</option><option value="hash">固定来源</option></select></label><div className="modal-columns"><label className="modal-field">流量上限（GB，0不限）<input type="number" min="0" value={values.flow} onChange={(event) => update("flow", event.target.value)} /></label><div className="modal-field"><span>已用流量</span><strong className="form-readonly">{formatBytes(used)}</strong><small className="field-help">流量按上传与下载合计计算，超限后自动暂停。</small></div></div><label className="modal-field">绑定网卡（可选）<input value={values.interfaceName} onChange={(event) => update("interfaceName", event.target.value)} placeholder="例如 eth0" /></label><div className="modal-actions"><button className="button button-quiet" type="button" onClick={onDiagnose}><Activity size={15} /> 诊断链路</button><button className="button button-quiet" type="button" onClick={onClose}>取消</button><button className="button button-primary" type="submit"><Check size={15} /> 保存转发</button></div></form></section></div>;
}

function TunnelEditModal({ tunnel, onClose, onSubmit, onDiagnose, onDelete }: { tunnel: ApiItem; onClose: () => void; onSubmit: (values: Record<string, string>) => Promise<boolean>; onDiagnose: () => void; onDelete: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({ name: tunnel.name || "", flowType: String(tunnel.flowType || tunnel.flow || 2), flowLimitGb: String(tunnel.flowLimitGb || 0), trafficRatio: String(tunnel.trafficRatio || 1), speedLimitKbps: String(tunnel.speedLimitKbps || 0), protocol: tunnel.protocol || "tls", anyTlsPassword: "", tcpListenAddr: tunnel.tcpListenAddr || "[::]", udpListenAddr: tunnel.udpListenAddr || "[::]", interfaceName: tunnel.interfaceName || "" });
  const update = (key: string, value: string) => setValues((current) => ({ ...current, [key]: value }));
  const submit = async (event: FormEvent) => { event.preventDefault(); await onSubmit(values); };
  const relay = Number(tunnel.type) === 2;
  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><section className="create-modal" role="dialog" aria-modal="true"><div className="modal-head"><div><span className="eyebrow">TUNNEL SETTINGS</span><h3>管理隧道 · {tunnel.name || `Tunnel-${tunnel.id}`}</h3></div><button className="icon-button" type="button" onClick={onClose} title="关闭"><X size={17} /></button></div><form onSubmit={submit} className="modal-form"><label className="modal-field">隧道名称<input required value={values.name} onChange={(event) => update("name", event.target.value)} /></label><div className="modal-columns"><label className="modal-field">流量记录类型<select value={values.flowType} onChange={(event) => update("flowType", event.target.value)}><option value="1">单向记录（仅入口到目标）</option><option value="2">双向记录（入口与返回合计）</option></select></label><label className="modal-field">隧道流量上限（GB）<input type="number" min="0" value={values.flowLimitGb} onChange={(event) => update("flowLimitGb", event.target.value)} /><small className="field-help">填写 0 表示不限。</small></label></div><div className="modal-columns"><label className="modal-field">流量统计倍率<input required type="number" min="0.1" step="0.1" value={values.trafficRatio} onChange={(event) => update("trafficRatio", event.target.value)} /><small className="field-help">例如 1.5 表示实际流量按 1.5 倍计入统计。</small></label><label className="modal-field">转发限速（KB/s）<input type="number" min="0" value={values.speedLimitKbps} onChange={(event) => update("speedLimitKbps", event.target.value)} /></label></div><label className="modal-field">协议<select value={values.protocol} disabled={!relay} onChange={(event) => update("protocol", event.target.value)}><option value="tls">TLS</option><option value="tcp">TCP</option>{relay && <option value="anytls">AnyTLS</option>}</select></label>{values.protocol === "anytls" && <label className="modal-field">AnyTLS 密码<input type="password" value={values.anyTlsPassword} onChange={(event) => update("anyTlsPassword", event.target.value)} placeholder="留空表示保持原密码" /></label>}<div className="modal-columns"><label className="modal-field">TCP 监听地址<input value={values.tcpListenAddr} onChange={(event) => update("tcpListenAddr", event.target.value)} /></label><label className="modal-field">UDP 监听地址<input value={values.udpListenAddr} onChange={(event) => update("udpListenAddr", event.target.value)} /></label></div><label className="modal-field">绑定网卡（可选）<input value={values.interfaceName} onChange={(event) => update("interfaceName", event.target.value)} placeholder="例如 eth0" /></label><div className="modal-actions"><button className="button button-quiet" type="button" onClick={onDiagnose}><Activity size={15} /> 诊断链路</button><button className="button button-quiet danger-text" type="button" onClick={onDelete}><X size={15} /> 删除</button><button className="button button-quiet" type="button" onClick={onClose}>取消</button><button className="button button-primary" type="submit"><Check size={15} /> 保存并同步</button></div></form></section></div>;
}

function App() {
  const location = useLocation();
  useEffect(() => { document.title = "RelayForge / 铸流"; }, []);
  if (!isLoggedIn() && location.pathname !== "/") return <LoginPage />;
  if (!isLoggedIn()) return <LoginPage />;
  return <Workspace />;
}

export default App;
