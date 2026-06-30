export interface EnumOptionDto {
  value: number;
  name: string;
}

export interface EnumMetadataDto {
  enums: Record<string, EnumOptionDto[]>;
}
