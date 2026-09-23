import HttpRequest from '@/libs/axios'
import config from '@/config'
const axios = new HttpRequest(config.baseUrl.dev)
export default axios