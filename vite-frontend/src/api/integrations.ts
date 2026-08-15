import Network from "./network";
import type { ApiPayload } from "@/types/api";
import type { ApiItem } from "@/types/app";

export const getXuiConnections = () => Network.post<ApiItem[]>("/xui/list");
export const getXuiInbounds = () => Network.post<ApiItem[]>("/xui/inbounds");
export const createXuiConnection = (data: ApiPayload) => Network.post<{ inboundCount?: number }>("/xui/create", data);
export const syncXuiConnection = (id: number) => Network.post<{ inboundCount?: number }>("/xui/sync", { id });
export const deleteXuiConnection = (id: number) => Network.post("/xui/delete", { id });
