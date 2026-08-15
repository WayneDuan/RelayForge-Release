import Network from "./network";
import type { ApiPayload } from "@/types/api";
import type { ApiItem } from "@/types/app";

export const createForward = (data: ApiPayload) => Network.post<{ existing?: boolean }>("/forward/create", data);
export const getForwardList = () => Network.post<ApiItem[]>("/forward/list");
export const updateForward = (data: ApiPayload) => Network.post("/forward/update", data);
export const deleteForward = (id: number) => Network.post("/forward/delete", { id });
export const forceDeleteForward = (id: number) => Network.post("/forward/force-delete", { id });
export const pauseForwardService = (forwardId: number) => Network.post("/forward/pause", { id: forwardId });
export const resumeForwardService = (forwardId: number) => Network.post("/forward/resume", { id: forwardId });
export const diagnoseForward = (forwardId: number) => Network.post("/forward/diagnose", { forwardId });
export const updateForwardOrder = (data: { forwards: Array<{ id: number; inx: number }> }) => Network.post("/forward/update-order", data);
