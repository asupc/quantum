import axios from '@/libs/api.request'

/**
 * 获取书签列表
 * @param {Object} params 查询参数
 * @returns Promise
 */
export const getBookmarks = (params) => {
  return axios.request({
    url: '/api/bookmark',
    method: 'get',
    params
  })
}

/**
 * 获取书签详情
 * @param {Number} id 书签ID
 * @returns Promise
 */
export const getBookmark = (id) => {
  return axios.request({
    url: `/api/bookmark/${id}`,
    method: 'get'
  })
}

/**
 * 创建书签
 * @param {Object} data 书签数据
 * @returns Promise
 */
export const createBookmark = (data) => {
  return axios.request({
    url: '/api/bookmark',
    method: 'post',
    data
  })
}

/**
 * 更新书签
 * @param {Number} id 书签ID
 * @param {Object} data 书签数据
 * @returns Promise
 */
export const updateBookmark = (id, data) => {
  return axios.request({
    url: `/api/bookmark/${id}`,
    method: 'put',
    data
  })
}

/**
 * 删除书签
 * @param {Number} id 书签ID
 * @returns Promise
 */
export const deleteBookmark = (id) => {
  return axios.request({
    url: `/api/bookmark/${id}`,
    method: 'delete'
  })
}

/**
 * 批量导入书签
 * @param {Array} data 书签数组
 * @returns Promise
 */
export const importBookmarks = (data) => {
  return axios.request({
    url: '/api/bookmark/import',
    method: 'post',
    data
  })
}

/**
 * 导出所有书签
 * @returns Promise
 */
export const exportBookmarks = () => {
  return axios.request({
    url: '/api/bookmark/export',
    method: 'get'
  })
}

/**
 * 更新书签排序
 * @param {Array} data 排序数据
 * @returns Promise
 */
export const updateSortOrder = (data) => {
  return axios.request({
    url: '/api/bookmark/sort',
    method: 'post',
    data
  })
}

export default {
  getBookmarks,
  getBookmark,
  createBookmark,
  updateBookmark,
  deleteBookmark,
  importBookmarks,
  exportBookmarks,
  updateSortOrder
}