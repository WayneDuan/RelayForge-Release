import { useEffect, useState, type FormEvent } from "react";
import toast from "react-hot-toast";

import { Plus, RefreshCw, Router, X } from "@/components/ui-icons";
import { createXuiConnection, deleteXuiConnection, getXuiConnections, getXuiInbounds, syncXuiConnection } from "@/api";
import type { ApiItem } from "@/types/app";

type XuiForm = {
  name: string;
  panelUrl: string;
  connectHost: string;
  apiToken: string;
  username: string;
  password: string;
  twoFactorCode: string;
  verifyTls: boolean;
};

const emptyForm: XuiForm = { name: "", panelUrl: "", connectHost: "", apiToken: "", username: "", password: "", twoFactorCode: "", verifyTls: true };

export default function XuiView({ onChanged }: { onChanged?: () => void }) {
  const [connections, setConnections] = useState<ApiItem[]>([]);
  const [inbounds, setInbounds] = useState<ApiItem[]>([]);
  const [form, setForm] = useState<XuiForm>(emptyForm);
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(false);

  const loadConnections = async () => {
    setLoading(true);
    try {
      const [connectionResponse, inboundResponse] = await Promise.all([getXuiConnections(), getXuiInbounds()]);
      if (connectionResponse.code === 0) setConnections(connectionResponse.data || []);
      else toast.error(connectionResponse.msg || "无法读取 3x-ui 集成");
      if (inboundResponse.code === 0) setInbounds(inboundResponse.data || []);
      else toast.error(inboundResponse.msg || "无法读取 3x-ui 入站");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { void loadConnections(); }, []);

  const updateField = <K extends keyof XuiForm>(key: K, value: XuiForm[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (!form.name.trim() || !form.panelUrl.trim() || (!form.apiToken.trim() && (!form.username.trim() || !form.password))) {
      toast.error("请填写名称、面板地址，以及 API Token 或账号密码");
      return;
    }
    setSaving(true);
    try {
      const response = await createXuiConnection({ ...form, name: form.name.trim(), panelUrl: form.panelUrl.trim(), connectHost: form.connectHost.trim() });
      if (response.code !== 0) {
        toast.error(response.msg || "3x-ui 接入失败");
        return;
      }
      setForm(emptyForm);
      await loadConnections();
      onChanged?.();
      toast.success(`3x-ui 已接入，发现 ${response.data?.inboundCount || 0} 个入站`);
    } finally {
      setSaving(false);
    }
  };

  const sync = async (connection: ApiItem) => {
    const response = await syncXuiConnection(Number(connection.id));
    if (response.code !== 0) {
      toast.error(response.msg || "3x-ui 同步失败");
      return;
    }
    await loadConnections();
    onChanged?.();
    toast.success(`已同步 ${response.data?.inboundCount || 0} 个入站`);
  };

  const remove = async (connection: ApiItem) => {
    if (!window.confirm(`确定删除 3x-ui 集成“${connection.name || connection.id}”吗？`)) return;
    const response = await deleteXuiConnection(Number(connection.id));
    if (response.code !== 0) {
      toast.error(response.msg || "删除失败");
      return;
    }
    await loadConnections();
    onChanged?.();
    toast.success("3x-ui 集成已删除");
  };

  const connectionInbounds = (connection: ApiItem) => inbounds.filter((inbound) => Number(inbound.connectionId) === Number(connection.id));

  return <>
    <section className="page-intro">
      <div>
        <p className="eyebrow">INTEGRATIONS / 05</p>
        <h2>3x-ui 集成</h2>
        <p className="muted">接入 3x-ui 面板并同步入站，新建转发时可直接选择目标入口。</p>
      </div>
      <button className="button button-quiet" type="button" onClick={() => void loadConnections()} disabled={loading}><RefreshCw size={15} className={loading ? "spin" : ""} /> 刷新列表</button>
    </section>
    <section className="settings-grid xui-page-grid">
      <div className="panel settings-card">
        <div className="setting-title"><Router size={18} /><div><h3>添加 3x-ui 面板</h3><p>凭据会由后端加密保存，只用于同步入站列表。</p></div></div>
        <form className="settings-form" onSubmit={save}>
          <label className="modal-field">集成名称<input required value={form.name} onChange={(event) => updateField("name", event.target.value)} placeholder="例如：东京 3x-ui" /></label>
          <label className="modal-field">3x-ui 面板地址<input required value={form.panelUrl} onChange={(event) => updateField("panelUrl", event.target.value)} placeholder="https://panel.example.com/your-path" /></label>
          <label className="modal-field">入站连接地址（可选）<input value={form.connectHost} onChange={(event) => updateField("connectHost", event.target.value)} placeholder="留空自动使用面板主机名" /><small className="field-help">这是 Agent 实际连接入站端口的地址，不是面板 API 地址。</small></label>
          <label className="modal-field">API Token（推荐）<input value={form.apiToken} onChange={(event) => updateField("apiToken", event.target.value)} type="password" placeholder="从 3x-ui 安全设置复制" /></label>
          <div className="modal-columns"><label className="modal-field">账号<input value={form.username} onChange={(event) => updateField("username", event.target.value)} autoComplete="off" placeholder="Token 为空时使用" /></label><label className="modal-field">密码<input value={form.password} onChange={(event) => updateField("password", event.target.value)} type="password" autoComplete="new-password" placeholder="Token 为空时使用" /></label></div>
          <label className="modal-field">2FA 验证码（可选）<input value={form.twoFactorCode} onChange={(event) => updateField("twoFactorCode", event.target.value)} inputMode="numeric" autoComplete="one-time-code" placeholder="账号模式且启用两步验证时填写" /></label>
          <label className="setting-toggle"><input type="checkbox" checked={form.verifyTls} onChange={(event) => updateField("verifyTls", event.target.checked)} /><span><strong>验证 TLS 证书</strong><small>自签名证书可关闭，公网面板建议保持开启。</small></span></label>
          <button className="button button-primary" disabled={saving}><Plus size={15} /> {saving ? "连接中..." : "接入并同步"}</button>
        </form>
      </div>
      <div className="panel settings-card">
        <div className="setting-title"><Router size={18} /><div><h3>已接入的面板</h3><p>同步后，面板入站会出现在新建转发的目标选择器中。</p></div><span className="tag green">{connections.length} 个连接</span></div>
        <div className="xui-connection-list">
          {connections.length === 0 ? <div className="activity-empty"><Router size={18} /><span>还没有 3x-ui 集成</span></div> : connections.map((connection) => {
            const connectionInboundList = connectionInbounds(connection);
            return <div className="xui-connection-item" key={connection.id}>
              <div className="xui-connection-head"><span><strong>{connection.name || `3x-ui-${connection.id}`}</strong><small className="table-secondary">{connection.connectHost} · {connection.inboundCount || 0} 个入站</small>{connection.lastError && <small className="danger-text">{connection.lastError}</small>}</span><span className="xui-row-actions"><span className={`node-status ${Number(connection.status) === 1 ? "online" : "offline"}`}>{Number(connection.status) === 1 ? "已连接" : "异常"}</span><button className="text-button" type="button" onClick={() => void sync(connection)}><RefreshCw size={13} /> 同步</button><button className="text-button danger-text" type="button" onClick={() => void remove(connection)}><X size={13} /> 删除</button></span></div>
              <div className="xui-inbound-details"><div className="xui-inbound-heading"><span>入站明细</span><strong>{connectionInboundList.length} 个</strong></div>{connectionInboundList.length === 0 ? <small className="table-secondary">暂无可显示的入站，请重新同步面板。</small> : connectionInboundList.map((inbound) => <div className={`xui-inbound-row ${inbound.enabled ? "" : "disabled"}`} key={inbound.id}><div className="xui-inbound-main"><span className={`xui-inbound-status ${inbound.enabled ? "enabled" : "disabled"}`}><i />{inbound.enabled ? "启用" : "停用"}</span><div><strong>{inbound.name || inbound.tag || `入站-${inbound.id}`}</strong><small>{inbound.tag && inbound.tag !== inbound.name ? inbound.tag : ""} · {String(inbound.protocol || "-").toUpperCase()}</small></div></div><div className="xui-inbound-address"><strong className="mono">{inbound.remoteAddr || "地址未返回"}</strong><small>监听 {inbound.listen || "默认"} · 面板端口 {inbound.port || "-"}</small></div></div>)}</div>
            </div>;
          })}
        </div>
      </div>
    </section>
  </>;
}
