<template>
  <error-content code="404" desc="Oh~~您的页面好像飞走了~" />
</template>

<script>
  import errorContent from './error-content.vue'
  export default {
    name: 'error_404',
    components: {
      errorContent
    },
    data() {
      return {
      }
    }
  }
</script>
