<template>
  <system-setting
    ref="systemSetting"
    @save="SystemSettingModal.Show = false"
  ></system-setting>
</template>     
<script>
import SystemSetting from "@/view/components/setting.vue";
export default {
  name: "SettingsIndex",
  data() {
    return {};
  },
  components: {
    SystemSetting,
  },
  mounted() {
    this.$refs.systemSetting.getSystemConfig();
  },
};
</script>