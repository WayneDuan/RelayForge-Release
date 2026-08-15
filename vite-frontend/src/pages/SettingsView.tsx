import { useEffect, useState, type FormEvent } from "react";
import toast from "react-hot-toast";

import { Bell, Check, ShieldCheck, Zap } from "@/components/ui-icons";
import { disableTwoFactor, enableTwoFactor, getConfigs, getTelegramSettings, getTwoFactorStatus, saveTelegramSettings, setupTwoFactor, testTelegramSettings, updateConfigs, updatePassword } from "@/api";

export default function SettingsView() {
  const [host, setHost] = useState(() => window.location.hostname);
  const [frontendPort, setFrontendPort] = useState("6311");
  const [port, setPort] = useState("6315");
  const [secure, setSecure] = useState(false);
  const [securePort, setSecurePort] = useState("443");
  const [saving, setSaving] = useState(false);
  const [configLoaded, setConfigLoaded] = useState(false);
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordSaving, setPasswordSaving] = useState(false);
  const [twoFactorEnabled, setTwoFactorEnabled] = useState(false);
  const [twoFactorSetup, setTwoFactorSetup] = useState<{ secret: string; otpauthUri: string } | null>(null);
  const [twoFactorPassword, setTwoFactorPassword] = useState("");
  const [twoFactorCode, setTwoFactorCode] = useState("");
  const [twoFactorBusy, setTwoFactorBusy] = useState(false);
  const [telegramEnabled, setTelegramEnabled] = useState(false);
  const [telegramConfigured, setTelegramConfigured] = useState(false);
  const [telegramToken, setTelegramToken] = useState("");
  const [telegramChatId, setTelegramChatId] = useState("");
  const [telegramThreshold, setTelegramThreshold] = useState("80");
  const [telegramNotifyFlow, setTelegramNotifyFlow] = useState(true);
  const [telegramNotifyNode, setTelegramNotifyNode] = useState(true);
  const [telegramSaving, setTelegramSaving] = useState(false);
  const [telegramTesting, setTelegramTesting] = useState(false);

  useEffect(() => {
    void getConfigs().then((response) => {
      if (response.code !== 0 || !response.data) {
        toast.error(response.msg || "无法读取服务器配置");
        return;
      }
      if (response.data.panel_host) setHost(response.data.panel_host);
      if (response.data.frontend_port) setFrontendPort(response.data.frontend_port);
      if (response.data.backend_port) setPort(response.data.backend_port);
      if (response.data.panel_secure) setSecure(response.data.panel_secure === "1" || response.data.panel_secure === "true");
      if (response.data.secure_port) setSecurePort(response.data.secure_port);
      setConfigLoaded(true);
    });
  }, []);

  useEffect(() => {
    void getTwoFactorStatus().then((response) => {
      if (response.code === 0) setTwoFactorEnabled(response.data?.enabled === true);
    });
  }, []);

  useEffect(() => {
    void getTelegramSettings().then((response) => {
      if (response.code !== 0 || !response.data) {
        toast.error(response.msg || "无法读取 Telegram 通知设置");
        return;
      }
      setTelegramEnabled(response.data.enabled === true);
      setTelegramConfigured(response.data.botTokenConfigured === true);
      setTelegramChatId(response.data.chatId || "");
      setTelegramThreshold(String(response.data.trafficThresholdPercent || 80));
      setTelegramNotifyFlow(response.data.notifyFlow !== false);
      setTelegramNotifyNode(response.data.notifyNode !== false);
    });
  }, []);

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (!host.trim() || !/^\d{1,5}$/.test(frontendPort) || !/^\d{1,5}$/.test(port) || !/^\d{1,5}$/.test(securePort) || Number(frontendPort) < 1 || Number(frontendPort) > 65535 || Number(port) < 1 || Number(port) > 65535 || Number(securePort) < 1 || Number(securePort) > 65535) {
      toast.error("请输入有效的面板地址和后端端口");
      return;
    }
    setSaving(true);
    try {
      const response = await updateConfigs({ panel_host: host.trim(), frontend_port: frontendPort, backend_port: port, panel_secure: secure ? "1" : "0", secure_port: securePort });
      if (response.code === 0) toast.success("站点配置已保存，新的安装命令将使用该地址");
      else toast.error(response.msg || "保存失败");
    } finally {
      setSaving(false);
    }
  };

  const nodeAddress = secure ? `wss://${host}:${securePort}` : `ws://${host}:${port}`;

  const saveTelegram = async (event: FormEvent) => {
    event.preventDefault();
    const threshold = Number(telegramThreshold);
    if (!Number.isInteger(threshold) || threshold < 1 || threshold > 100 || (telegramEnabled && (!telegramChatId.trim() || (!telegramConfigured && !telegramToken.trim())))) {
      toast.error("启用 Telegram 需要 Bot Token、Chat ID，阈值必须是 1-100");
      return;
    }
    setTelegramSaving(true);
    try {
      const response = await saveTelegramSettings({
        enabled: telegramEnabled,
        botToken: telegramToken.trim() || undefined,
        chatId: telegramChatId.trim(),
        trafficThresholdPercent: threshold,
        notifyFlow: telegramNotifyFlow,
        notifyNode: telegramNotifyNode,
      });
      if (response.code !== 0) {
        toast.error(response.msg || "Telegram 设置保存失败");
        return;
      }
      setTelegramConfigured(telegramConfigured || Boolean(telegramToken.trim()));
      setTelegramToken("");
      toast.success("Telegram 通知设置已保存");
    } finally {
      setTelegramSaving(false);
    }
  };

  const testTelegram = async () => {
    setTelegramTesting(true);
    try {
      const response = await testTelegramSettings();
      if (response.code === 0) toast.success(response.msg || "测试消息已发送");
      else toast.error(response.msg || "Telegram 测试失败");
    } finally {
      setTelegramTesting(false);
    }
  };

  const changePassword = async (event: FormEvent) => {
    event.preventDefault();
    if (!currentPassword || newPassword.length < 6 || newPassword !== confirmPassword) {
      toast.error("请输入当前密码，新密码至少 6 位且两次输入一致");
      return;
    }
    setPasswordSaving(true);
    try {
      const response = await updatePassword({ currentPassword, newPassword, confirmPassword });
      if (response.code !== 0) {
        toast.error(response.msg || "密码修改失败");
        return;
      }
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
      toast.success("密码已修改，下次登录请使用新密码");
    } finally {
      setPasswordSaving(false);
    }
  };

  const beginTwoFactorSetup = async () => {
    setTwoFactorBusy(true);
    try {
      const response = await setupTwoFactor();
      if (response.code !== 0) {
        toast.error(response.msg || "无法生成 2FA 密钥");
        return;
      }
      setTwoFactorSetup(response.data);
      setTwoFactorPassword("");
      setTwoFactorCode("");
    } finally {
      setTwoFactorBusy(false);
    }
  };

  const enableTotp = async (event: FormEvent) => {
    event.preventDefault();
    if (!twoFactorSetup || !twoFactorPassword || twoFactorCode.length !== 6) {
      toast.error("请输入当前密码和认证器中的 6 位验证码");
      return;
    }
    setTwoFactorBusy(true);
    try {
      const response = await enableTwoFactor({ currentPassword: twoFactorPassword, secret: twoFactorSetup.secret, code: twoFactorCode });
      if (response.code !== 0) {
        toast.error(response.msg || "2FA 启用失败");
        return;
      }
      setTwoFactorEnabled(true);
      setTwoFactorSetup(null);
      setTwoFactorPassword("");
      setTwoFactorCode("");
      toast.success("2FA 已启用");
    } finally {
      setTwoFactorBusy(false);
    }
  };

  const disableTotp = async (event: FormEvent) => {
    event.preventDefault();
    if (!twoFactorPassword || twoFactorCode.length !== 6) {
      toast.error("请输入当前密码和认证器中的 6 位验证码");
      return;
    }
    setTwoFactorBusy(true);
    try {
      const response = await disableTwoFactor({ currentPassword: twoFactorPassword, code: twoFactorCode });
      if (response.code !== 0) {
        toast.error(response.msg || "2FA 关闭失败");
        return;
      }
      setTwoFactorEnabled(false);
      setTwoFactorPassword("");
      setTwoFactorCode("");
      toast.success("2FA 已关闭");
    } finally {
      setTwoFactorBusy(false);
    }
  };

  const copyTwoFactorSecret = async () => {
    if (!twoFactorSetup) return;
    await navigator.clipboard.writeText(twoFactorSetup.secret);
    toast.success("2FA 密钥已复制");
  };

  return <>
    <section className="page-intro">
      <div>
        <p className="eyebrow">CONTROL PLANE / 06</p>
        <h2>面板设置</h2>
        <p className="muted">管理控制面连接地址与部署安全参数。</p>
      </div>
    </section>
    <section className="settings-grid">
      <div className="panel settings-card">
        <div className="setting-title">
          <ShieldCheck size={18} />
          <div><h3>节点安装地址</h3><p>复制安装命令时，Agent 将连接到这里的后端地址。</p></div>
          <span className="tag green">{configLoaded ? "SERVER SAVED" : "LOADING"}</span>
        </div>
        <form className="settings-form" onSubmit={save}>
          <label className="modal-field">面板主机名或 IP<input value={host} onChange={(event) => setHost(event.target.value)} placeholder="panel.example.com" /></label>
          <label className="modal-field">前端端口<input value={frontendPort} onChange={(event) => setFrontendPort(event.target.value)} inputMode="numeric" placeholder="6311" /></label>
          <label className="modal-field">后端端口<input value={port} onChange={(event) => setPort(event.target.value)} inputMode="numeric" placeholder="6315" /></label>
          <label className="setting-toggle"><input type="checkbox" checked={secure} onChange={(event) => setSecure(event.target.checked)} /><span><strong>开启加密连接（WSS）</strong><small>适用于 Cloudflare 橙云代理，通常使用 HTTPS 端口 443。</small></span></label>
          {secure && <label className="modal-field">WSS 端口<input value={securePort} onChange={(event) => setSecurePort(event.target.value)} inputMode="numeric" placeholder="443" /></label>}
          <button className="button button-primary" disabled={saving || !configLoaded}><Check size={15} /> {saving ? "保存中..." : "保存站点配置"}</button>
        </form>
        <div className="setting-row"><span>当前节点地址</span><strong className="green-text">{nodeAddress}</strong></div>
      </div>
      <div className="panel settings-card">
        <div className="setting-title"><Zap size={18} /><div><h3>维护策略</h3><p>面向私人部署的默认维护偏好</p></div></div>
        <div className="setting-row"><span>节点 WebSocket</span><strong className="green-text">AES-256-GCM</strong></div>
        <div className="setting-row"><span>节点心跳</span><strong>实时推送</strong></div>
        <div className="setting-row"><span>诊断超时</span><strong>5 秒</strong></div>
        <div className="setting-row"><span>审计保留</span><strong>最近 5 条</strong></div>
      </div>
      <div className="panel settings-card">
        <div className="setting-title"><ShieldCheck size={18} /><div><h3>账号安全</h3><p>修改当前管理员账号的登录密码。</p></div></div>
        <form className="settings-form" onSubmit={changePassword}>
          <label className="modal-field">当前密码<input required type="password" value={currentPassword} onChange={(event) => setCurrentPassword(event.target.value)} autoComplete="current-password" /></label>
          <label className="modal-field">新密码<input required type="password" minLength={6} value={newPassword} onChange={(event) => setNewPassword(event.target.value)} autoComplete="new-password" /></label>
          <label className="modal-field">确认新密码<input required type="password" minLength={6} value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} autoComplete="new-password" /></label>
          <button className="button button-primary" type="submit" disabled={passwordSaving}><Check size={15} /> {passwordSaving ? "修改中..." : "修改登录密码"}</button>
        </form>
      </div>
      <div className="panel settings-card">
        <div className="setting-title"><ShieldCheck size={18} /><div><h3>两步验证（2FA）</h3><p>使用认证器动态验证码保护面板登录。</p></div><span className={`tag ${twoFactorEnabled ? "green" : ""}`}>{twoFactorEnabled ? "已启用" : "未启用"}</span></div>
        {!twoFactorEnabled && !twoFactorSetup && <div className="settings-form"><p className="field-help">启用后，登录时除了密码还需要输入认证器生成的 6 位验证码。</p><button className="button button-primary" type="button" onClick={() => void beginTwoFactorSetup()} disabled={twoFactorBusy}><ShieldCheck size={15} /> {twoFactorBusy ? "生成中..." : "开始设置 2FA"}</button></div>}
        {!twoFactorEnabled && twoFactorSetup && <form className="settings-form" onSubmit={enableTotp}><div className="two-factor-secret"><span>设置密钥</span><strong className="mono">{twoFactorSetup.secret}</strong><button className="text-button" type="button" onClick={() => void copyTwoFactorSecret()}>复制密钥</button></div><label className="modal-field">认证器地址（也可用此 URI 导入）<input readOnly value={twoFactorSetup.otpauthUri} /></label><label className="modal-field">当前密码<input required type="password" value={twoFactorPassword} onChange={(event) => setTwoFactorPassword(event.target.value)} autoComplete="current-password" /></label><label className="modal-field">认证器验证码<input required value={twoFactorCode} onChange={(event) => setTwoFactorCode(event.target.value.replace(/\D/g, "").slice(0, 6))} inputMode="numeric" autoComplete="one-time-code" placeholder="6 位验证码" /></label><div className="modal-actions"><button className="button button-quiet" type="button" onClick={() => setTwoFactorSetup(null)} disabled={twoFactorBusy}>取消设置</button><button className="button button-primary" type="submit" disabled={twoFactorBusy}><Check size={15} /> {twoFactorBusy ? "验证中..." : "启用 2FA"}</button></div></form>}
        {twoFactorEnabled && <form className="settings-form" onSubmit={disableTotp}><p className="field-help">关闭 2FA 需要当前密码和一次有效的认证器验证码。</p><label className="modal-field">当前密码<input required type="password" value={twoFactorPassword} onChange={(event) => setTwoFactorPassword(event.target.value)} autoComplete="current-password" /></label><label className="modal-field">认证器验证码<input required value={twoFactorCode} onChange={(event) => setTwoFactorCode(event.target.value.replace(/\D/g, "").slice(0, 6))} inputMode="numeric" autoComplete="one-time-code" placeholder="6 位验证码" /></label><button className="button button-quiet danger-text" type="submit" disabled={twoFactorBusy}>{twoFactorBusy ? "关闭中..." : "关闭 2FA"}</button></form>}
      </div>
      <div className="panel settings-card">
        <div className="setting-title"><Bell size={18} /><div><h3>Telegram 通知</h3><p>接收流量阈值、额度用尽和节点状态通知。</p></div><span className={`tag ${telegramEnabled ? "green" : ""}`}>{telegramEnabled ? "已启用" : "未启用"}</span></div>
        <form className="settings-form" onSubmit={saveTelegram}>
          <label className="setting-toggle"><input type="checkbox" checked={telegramEnabled} onChange={(event) => setTelegramEnabled(event.target.checked)} /><span><strong>启用 Telegram Bot</strong><small>通知由面板后端发送，Bot Token 不会返回到页面。</small></span></label>
          <label className="modal-field">Bot Token<input type="password" value={telegramToken} onChange={(event) => setTelegramToken(event.target.value)} placeholder={telegramConfigured ? "已保存，留空保持不变" : "从 @BotFather 获取"} autoComplete="off" /></label>
          <label className="modal-field">Chat ID<input value={telegramChatId} onChange={(event) => setTelegramChatId(event.target.value)} placeholder="例如 -1001234567890" /></label>
          <label className="modal-field">流量通知阈值（百分比）<input type="number" min="1" max="100" value={telegramThreshold} onChange={(event) => setTelegramThreshold(event.target.value)} inputMode="numeric" /></label>
          <label className="setting-toggle"><input type="checkbox" checked={telegramNotifyFlow} onChange={(event) => setTelegramNotifyFlow(event.target.checked)} /><span><strong>流量额度通知</strong><small>达到设定阈值和额度用尽时各通知一次，流量重置后可再次通知。</small></span></label>
          <label className="setting-toggle"><input type="checkbox" checked={telegramNotifyNode} onChange={(event) => setTelegramNotifyNode(event.target.checked)} /><span><strong>节点状态通知</strong><small>节点上线或离线时发送一条状态消息。</small></span></label>
          <div className="modal-actions"><button className="button button-primary" type="submit" disabled={telegramSaving}><Check size={15} /> {telegramSaving ? "保存中..." : "保存 Telegram 设置"}</button><button className="button button-quiet" type="button" onClick={() => void testTelegram()} disabled={telegramTesting || !telegramEnabled}><Bell size={15} /> {telegramTesting ? "发送中..." : "发送测试消息"}</button></div>
        </form>
      </div>
    </section>
  </>;
}
