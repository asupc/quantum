import axios from '@/libs/api.request';

// 容器管理
export const listContainers = (all = false) => {
    return axios.request({
        url: '/api/docker/containers',
        method: 'get',
        params: {
            all
        }
    });
};

export const inspectContainer = (containerId) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}`,
        method: 'get'
    });
};

export const createContainer = (config) => {
    return axios.request({
        url: '/api/docker/containers',
        method: 'post',
        data: config
    });
};

export const startContainer = (containerId) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/start`,
        method: 'post'
    });
};

export const stopContainer = (containerId) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/stop`,
        method: 'post'
    });
};

export const restartContainer = (containerId) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/restart`,
        method: 'post'
    });
};

export const removeContainer = (
    containerId,
    force = false,
    removeVolumes = false
) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}`,
        method: 'delete',
        params: {
            force,
            removeVolumes
        }
    });
};

export const getContainerLogs = (
    containerId,
    follow = false,
    stdout = true,
    stderr = true,
    tail = 'all'
) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/logs`,
        method: 'get',
        params: {
            follow,
            stdout,
            stderr,
            tail
        }
    });
};

export const executeCommand = (containerId, command) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/execute`,
        method: 'post',
        data: command
    });
};

/**
 * 获取容器的实时统计信息 (CPU, 内存, 网络等)
 * @param {string} containerId - 容器ID
 * @returns {Promise}
 */
export const getContainerStats = (containerId) => {
    return axios.request({
        url: `/api/docker/containers/${containerId}/stats`,
        method: 'get',
        noLoading: true
    });
};

/**
 * 批量获取容器的实时统计信息 (CPU, 内存, 网络等)
 * @param {Array<string>} containerIds - 容器ID数组
 * @returns {Promise}
 */
export const getContainersStats = (containerIds) => {
    return axios.request({
        url: '/api/docker/containers/stats',
        method: 'post',
        data: containerIds,
        noLoading: true
    });
};

// 镜像管理
export const listImages = (all = false) => {
    return axios.request({
        url: '/api/docker/images',
        method: 'get',
        params: {
            all
        }
    });
};

export const pullImage = (image, tag = 'latest') => {
    return axios.request({
        url: '/api/docker/images/pull',
        method: 'post',
        params: {
            image,
            tag
        }
    });
};

export const removeImage = (imageId, force = false) => {
    return axios.request({
        url: `/api/docker/images/${imageId}`,
        method: 'delete',
        params: {
            force
        }
    });
};

// 网络管理
export const listNetworks = () => {
    return axios.request({
        url: '/api/docker/networks',
        method: 'get'
    });
};

export const createNetwork = (name, driver = 'bridge') => {
    return axios.request({
        url: '/api/docker/networks',
        method: 'post',
        params: {
            name,
            driver
        }
    });
};

export const removeNetwork = (networkId) => {
    return axios.request({
        url: `/api/docker/networks/${networkId}`,
        method: 'delete'
    });
};

export const connectContainerToNetwork = (networkId, containerId) => {
    return axios.request({
        url: `/api/docker/networks/${networkId}/connect`,
        method: 'post',
        params: {
            containerId
        }
    });
};

export const disconnectContainerFromNetwork = (networkId, containerId) => {
    return axios.request({
        url: `/api/docker/networks/${networkId}/disconnect`,
        method: 'post',
        params: {
            containerId
        }
    });
};

// 卷管理
export const listVolumes = () => {
    return axios.request({
        url: '/api/docker/volumes',
        method: 'get'
    });
};

export const createVolume = (name, driver = 'local') => {
    return axios.request({
        url: '/api/docker/volumes',
        method: 'post',
        params: {
            name,
            driver
        }
    });
};

export const removeVolume = (volumeName) => {
    return axios.request({
        url: `/api/docker/volumes/${volumeName}`,
        method: 'delete'
    });
};