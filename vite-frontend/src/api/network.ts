import axios, { type AxiosRequestConfig, type AxiosResponse } from "axios";
import { getPanelAddresses, isWebViewFunc } from "@/utils/panel";
import type { ApiResponse } from "@/types/api";

interface PanelAddress {
  name: string;
  address: string;
  inx: boolean;
}

let baseURL = "";

const setPanelAddresses = (addresses: PanelAddress[]) => {
  const active = addresses.find((item) => item.inx);
  if (!active) return;
  baseURL = `${active.address}/api/v1/`;
  axios.defaults.baseURL = baseURL;
};

const getWebViewPanelAddress = () => {
  (window as unknown as Window & { setAddresses: (addresses: PanelAddress[]) => void }).setAddresses = setPanelAddresses;
  getPanelAddresses("setAddresses");
};

export const reinitializeBaseURL = () => {
  if (isWebViewFunc()) {
    getWebViewPanelAddress();
    return;
  }

  baseURL = import.meta.env.VITE_API_BASE ? `${import.meta.env.VITE_API_BASE}/api/v1/` : "/api/v1/";
  axios.defaults.baseURL = baseURL;
};

reinitializeBaseURL();

const handleTokenExpired = () => {
  window.localStorage.removeItem("token");
  window.localStorage.removeItem("role_id");
  window.localStorage.removeItem("name");
  if (window.location.pathname !== "/") window.location.href = "/";
};

const isTokenExpired = (response?: ApiResponse) => response?.code === 401;

const request = async <T>(method: "get" | "post", path: string, data: unknown): Promise<ApiResponse<T>> => {
  if (baseURL === "") {
    return { code: -1, msg: "请先设置面板地址", data: null as T };
  }

  const config: AxiosRequestConfig = {
    timeout: 30_000,
    headers: {
      Authorization: window.localStorage.getItem("token") || undefined,
      "Content-Type": "application/json",
    },
  };

  try {
    const response: AxiosResponse<ApiResponse<T>> = method === "get"
      ? await axios.get<ApiResponse<T>>(path, { ...config, params: data })
      : await axios.post<ApiResponse<T>>(path, data, config);
    if (isTokenExpired(response.data)) handleTokenExpired();
    return response.data;
  } catch (error) {
    if (axios.isAxiosError(error) && error.response?.status === 401) handleTokenExpired();
    const message = error instanceof Error ? error.message : "网络请求失败";
    return { code: -1, msg: message, data: null as T };
  }
};

const Network = {
  get: <T = unknown>(path = "", data: unknown = {}) => request<T>("get", path, data),
  post: <T = unknown>(path = "", data: unknown = {}) => request<T>("post", path, data),
};

export default Network;
