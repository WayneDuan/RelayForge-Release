import Network from "./network";
import type { ApiPayload } from "@/types/api";
import type { ApiItem } from "@/types/app";

export const createTunnel = (data: ApiPayload) => Network.post("/tunnel/create", data);
export const getTunnelList = () => Network.post<ApiItem[]>("/tunnel/list");
export const getTunnelById = (id: number) => Network.post<ApiItem>("/tunnel/get", { id });
export const updateTunnel = (data: ApiPayload) => Network.post("/tunnel/update", data);
export const deleteTunnel = (id: number) => Network.post("/tunnel/delete", { id });
export const diagnoseTunnel = (tunnelId: number) => Network.post("/tunnel/diagnose", { tunnelId });

export const assignUserTunnel = (data: ApiPayload) => Network.post("/tunnel/user/assign", data);
export const getUserTunnelList = (queryData: ApiPayload = {}) => Network.post<ApiItem[]>("/tunnel/user/list", queryData);
export const removeUserTunnel = (params: ApiPayload) => Network.post("/tunnel/user/remove", params);
export const updateUserTunnel = (data: ApiPayload) => Network.post("/tunnel/user/update", data);
export const userTunnel = () => Network.post<ApiItem[]>("/tunnel/user/tunnel");
