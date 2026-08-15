import Network from "./network";
import type { ApiPayload } from "@/types/api";
import type { ApiItem } from "@/types/app";

export const createUser = (data: ApiPayload) => Network.post("/user/create", data);
export const getAllUsers = (pageData: ApiPayload = {}) => Network.post<ApiItem[] | { list: ApiItem[] }>("/user/list", pageData);
export const updateUser = (data: ApiPayload) => Network.post("/user/update", data);
export const deleteUser = (id: number) => Network.post("/user/delete", { id });
export const getUserPackageInfo = () => Network.post("/user/package");
export const resetUserFlow = (data: { id: number; type: number }) => Network.post("/user/reset", data);
