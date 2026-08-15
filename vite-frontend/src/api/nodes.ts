import Network from "./network";
import type { ApiPayload } from "@/types/api";
import type { ApiItem } from "@/types/app";

export const createNode = (data: ApiPayload) => Network.post("/node/create", data);
export const getNodeList = () => Network.post<ApiItem[]>("/node/list");
export const updateNode = (data: ApiPayload) => Network.post("/node/update", data);
export const deleteNode = (id: number) => Network.post("/node/delete", { id });
export const getNodeInstallCommand = (id: number, platform: "linux" | "windows" = "linux") => Network.post<string>("/node/install", { id, platform });
export const checkNodeStatus = (nodeId?: number) => Network.post<ApiItem[]>("/node/check-status", nodeId ? { nodeId } : {});
