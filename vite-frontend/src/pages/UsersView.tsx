import { useState, type FormEvent } from "react";
import toast from "react-hot-toast";

import { Check, Plus, RefreshCw, Settings2, UserRound, X } from "@/components/ui-icons";
import { createUser, deleteUser, resetUserFlow, updateUser } from "@/api";
import type { ApiItem } from "@/types/app";

type UserForm = {
  user: string;
  pwd: string;
  flow: string;
  num: string;
  expTime: string;
  flowResetTime: string;
  status: boolean;
};

const defaultExpiry = () => {
  const date = new Date();
  date.setFullYear(date.getFullYear() + 1);
  return date.toISOString().slice(0, 10);
};

const emptyForm = (): UserForm => ({ user: "", pwd: "", flow: "0", num: "1", expTime: defaultExpiry(), flowResetTime: "1", status: true });

const dateValue = (value: unknown) => {
  const timestamp = Number(value || 0);
  return timestamp > 0 ? new Date(timestamp).toISOString().slice(0, 10) : "";
};

const formatDate = (value: unknown) => {
  const timestamp = Number(value || 0);
  return timestamp > 0 ? new Date(timestamp).toLocaleDateString("zh-CN") : "不限期";
};

const formatBytes = (value: unknown) => {
  const bytes = Number(value || 0);
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes < 1024 ** 4) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  return `${(bytes / 1024 ** 4).toFixed(2)} TB`;
};

export default function UsersView({ users, onChanged }: { users: ApiItem[]; onChanged?: () => void | Promise<void> }) {
  const [editingUser, setEditingUser] = useState<ApiItem | null>(null);
  const [form, setForm] = useState<UserForm>(emptyForm);
  const [saving, setSaving] = useState(false);
  const [editorOpen, setEditorOpen] = useState(false);

  const openCreate = () => {
    setEditingUser(null);
    setForm(emptyForm());
    setEditorOpen(true);
  };

  const openEdit = (user: ApiItem) => {
    setEditingUser(user);
    setEditorOpen(true);
    setForm({
      user: user.user || "",
      pwd: "",
      flow: String(user.flow || 0),
      num: String(user.num || 0),
      expTime: dateValue(user.expTime),
      flowResetTime: String(user.flowResetTime || 1),
      status: Number(user.status) === 1,
    });
  };

  const updateField = <K extends keyof UserForm>(key: K, value: UserForm[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const save = async (event: FormEvent) => {
    event.preventDefault();
    const flow = Number(form.flow);
    const num = Number(form.num);
    const resetDay = Number(form.flowResetTime);
    if (!form.user.trim() || (!editingUser && !form.pwd) || !Number.isFinite(flow) || flow < 0 || !Number.isFinite(num) || num < 0 || !Number.isInteger(resetDay) || resetDay < 1 || resetDay > 31) {
      toast.error("请填写有效的账号、密码、额度和重置日");
      return;
    }
    setSaving(true);
    try {
      const data = {
        ...(editingUser ? { id: Number(editingUser.id) } : {}),
        user: form.user.trim(),
        pwd: form.pwd,
        flow,
        num: Math.floor(num),
        expTime: form.expTime ? new Date(`${form.expTime}T23:59:59`).getTime() : 0,
        flowResetTime: resetDay,
        status: form.status ? 1 : 0,
      };
      const response = editingUser ? await updateUser(data) : await createUser(data);
      if (response.code !== 0) {
        toast.error(response.msg || (editingUser ? "用户更新失败" : "用户创建失败"));
        return;
      }
      await onChanged?.();
      setForm(emptyForm());
      setEditingUser(null);
      setEditorOpen(false);
      toast.success(editingUser ? "用户已更新" : "用户已创建");
    } finally {
      setSaving(false);
    }
  };

  const remove = async (user: ApiItem) => {
    if (!window.confirm(`确定删除用户“${user.user || user.id}”吗？该用户的转发、隧道权限和 3x-ui 集成也会被删除。`)) return;
    const response = await deleteUser(Number(user.id));
    if (response.code !== 0) {
      toast.error(response.msg || "用户删除失败");
      return;
    }
    await onChanged?.();
    toast.success("用户已删除");
  };

  const resetFlow = async (user: ApiItem) => {
    if (!window.confirm(`确定重置用户“${user.user || user.id}”的累计流量吗？`)) return;
    const response = await resetUserFlow({ id: Number(user.id), type: 1 });
    if (response.code !== 0) {
      toast.error(response.msg || "流量重置失败");
      return;
    }
    await onChanged?.();
    toast.success("用户流量已重置");
  };

  return <>
    <section className="page-intro">
      <div>
        <p className="eyebrow">IDENTITY / 05</p>
        <h2>用户管理</h2>
        <p className="muted">创建用户并分配账号额度，普通用户不会看到管理员控制项。</p>
      </div>
      <button className="button button-primary" type="button" onClick={openCreate}><Plus size={16} /> 新增用户</button>
    </section>
    <section className="panel table-panel users-panel">
      <div className="table-toolbar"><div><strong className="table-primary">用户账号</strong><small className="table-secondary">管理员账号不显示在普通用户列表中</small></div><span className="tag green">{users.length} 个用户</span></div>
      <div className="table-wrap"><table><thead><tr><th>状态</th><th>账号</th><th>额度</th><th>使用流量</th><th>到期</th><th>操作</th></tr></thead><tbody>{users.length === 0 ? <tr><td colSpan={6}><div className="empty-state"><UserRound size={18} /><span>还没有普通用户</span><button className="text-button" type="button" onClick={openCreate}>创建第一个用户</button></div></td></tr> : users.map((user) => <tr key={user.id}><td><span className={`table-status ${Number(user.status) === 1 ? "" : "danger-text"}`}><i className={`status-dot ${Number(user.status) === 1 ? "is-online" : "is-offline"}`} />{Number(user.status) === 1 ? "启用" : "停用"}</span></td><td><strong className="table-primary">{user.user || `user-${user.id}`}</strong><small className="table-secondary">最多 {user.num || 0} 条转发</small></td><td><strong className="table-primary">{Number(user.flow) > 0 ? `${user.flow} GB` : "不限流量"}</strong><small className="table-secondary">每月 {user.flowResetTime || 1} 日重置</small></td><td><strong className="table-primary">{formatBytes(Number(user.inFlow || 0) + Number(user.outFlow || 0))}</strong><small className="table-secondary">上行 {formatBytes(user.inFlow)} · 下行 {formatBytes(user.outFlow)}</small></td><td><span className="table-primary">{formatDate(user.expTime)}</span></td><td><div className="row-actions"><button className="mini-button" type="button" title="编辑用户" onClick={() => openEdit(user)}><Settings2 size={14} /></button><button className="mini-button" type="button" title="重置累计流量" onClick={() => void resetFlow(user)}><RefreshCw size={14} /></button><button className="mini-button danger" type="button" title="删除用户" onClick={() => void remove(user)}><X size={14} /></button></div></td></tr>)}</tbody></table></div>
    </section>
    {<div className="modal-backdrop" hidden={!editorOpen} onMouseDown={(event) => { if (event.target === event.currentTarget && !saving) { setEditingUser(null); setForm(emptyForm()); setEditorOpen(false); } }}><section className="create-modal user-modal" role="dialog" aria-modal="true" aria-label={editingUser ? "编辑用户" : "新增用户"}><div className="modal-head"><div><span className="eyebrow">IDENTITY / USER</span><h3>{editingUser ? `编辑用户 · ${editingUser.user}` : "新增用户"}</h3></div><button className="icon-button" type="button" onClick={() => { setEditingUser(null); setForm(emptyForm()); setEditorOpen(false); }} disabled={saving} title="关闭"><X size={17} /></button></div><form className="modal-form" onSubmit={save}><label className="modal-field">用户名<input required value={form.user} onChange={(event) => updateField("user", event.target.value)} autoComplete="off" placeholder="例如：customer01" /></label><label className="modal-field">{editingUser ? "新密码（留空表示不修改）" : "登录密码"}<input required={!editingUser} value={form.pwd} onChange={(event) => updateField("pwd", event.target.value)} type="password" autoComplete="new-password" placeholder={editingUser ? "留空保持当前密码" : "请输入登录密码"} /></label><div className="modal-columns"><label className="modal-field">总流量上限（GB，0 不限）<input required type="number" min="0" value={form.flow} onChange={(event) => updateField("flow", event.target.value)} /></label><label className="modal-field">最大转发数<input required type="number" min="0" value={form.num} onChange={(event) => updateField("num", event.target.value)} /></label></div><div className="modal-columns"><label className="modal-field">到期日期<input type="date" value={form.expTime} onChange={(event) => updateField("expTime", event.target.value)} /></label><label className="modal-field">每月重置日<input required type="number" min="1" max="31" value={form.flowResetTime} onChange={(event) => updateField("flowResetTime", event.target.value)} /></label></div><label className="setting-toggle"><input type="checkbox" checked={form.status} onChange={(event) => updateField("status", event.target.checked)} /><span><strong>启用账号</strong><small>停用后用户无法登录，但历史数据会保留。</small></span></label><div className="modal-actions"><button className="button button-quiet" type="button" onClick={() => { setEditingUser(null); setForm(emptyForm()); setEditorOpen(false); }} disabled={saving}>取消</button><button className="button button-primary" type="submit" disabled={saving}><Check size={15} /> {saving ? "保存中..." : "保存用户"}</button></div></form></section></div>}
  </>;
}
