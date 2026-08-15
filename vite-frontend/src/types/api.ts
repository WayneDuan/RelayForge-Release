export interface ApiResponse<T = unknown> {
  code: number;
  msg: string;
  data: T;
}

export type ApiPayload = Record<string, unknown>;
