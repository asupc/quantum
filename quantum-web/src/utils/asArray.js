/**
 * 列表载荷兜底：接口返回的列表字段（Sessions/Data/Messages 等）可能被异常页或
 * 非预期响应形态（对象/字符串/null）替换；`|| []` 只能挡假值，挡不住非数组对象，
 * 后续 .map/.forEach/.filter 会整页崩溃。统一收敛为数组，调用方无需各自判型。
 * @returns {Array} 非数组一律返回空数组（同引用返回，避免多一次拷贝）
 */
export function asArray(value) {
    return Array.isArray(value) ? value : [];
}
