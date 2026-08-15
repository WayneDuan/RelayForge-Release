export type ApiItem = Record<string, any>;
export type { ApiPayload } from "./api";

export type View = "dashboard" | "forwards" | "nodes" | "tunnels" | "users" | "xui" | "settings";

export type Creator = Exclude<View, "dashboard" | "settings" | "users" | "xui"> | null;
