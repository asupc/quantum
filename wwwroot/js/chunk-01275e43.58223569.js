(window["webpackJsonp"]=window["webpackJsonp"]||[]).push([["chunk-01275e43"],{"0f37":function(t,a,e){"use strict";e.d(a,"a",(function(){return o}));var n=e("66df"),o=function(){return n["a"].request({url:"/api/Enum",method:"get"})}},1826:function(t,a,e){"use strict";e("8cbb")},"1a87":function(t,a,e){"use strict";e("9e72")},3550:function(t,a,e){"use strict";e.r(a);var n=function(){var t=this,a=t._self._c;return a("div",[a("div",[a("Form",{attrs:{"label-width":80},nativeOn:{keydown:function(a){return!a.type.indexOf("key")&&t._k(a.keyCode,"enter",13,a.key,"Enter")?null:(a.preventDefault(),t.LoadTask(1))}}},[a("Row",[a("Col",{attrs:{span:"4"}},[a("FormItem",{attrs:{label:"搜索"}},[a("Input",{attrs:{placeholder:"输入搜索关键字"},model:{value:t.QueryForm.Key,callback:function(a){t.$set(t.QueryForm,"Key",a)},expression:"QueryForm.Key"}})],1)],1),t._v("\n         \n        "),a("Col",{attrs:{span:"4"}},[a("FormItem",{attrs:{label:"分组"}},[a("Select",{attrs:{clearable:""},on:{"on-change":t.taskGroupSelectChange},model:{value:t.QueryForm.TaskGroupId,callback:function(a){t.$set(t.QueryForm,"TaskGroupId",a)},expression:"QueryForm.TaskGroupId"}},t._l(t.taskGroups,(function(e){return a("Option",{key:e.Id,attrs:{value:e.Id}},[t._v(t._s(e.Name))])})),1)],1)],1),a("Col",{attrs:{span:"4"}},[a("FormItem",{attrs:{label:"状态"}},[a("Select",{attrs:{clearable:""},on:{"on-change":function(a){return t.LoadTask(1)}},model:{value:t.QueryForm.Enable,callback:function(a){t.$set(t.QueryForm,"Enable",a)},expression:"QueryForm.Enable"}},[a("Option",{attrs:{value:"true"}},[t._v("启用")]),a("Option",{attrs:{value:"false"}},[t._v("禁用")])],1)],1)],1),a("Button",{attrs:{type:"info"},on:{click:function(a){return t.LoadTask(1)}}},[a("Icon",{attrs:{type:"md-search"}}),t._v(" 搜索\n        ")],1)],1)],1)],1),a("div",{staticStyle:{margin:"20px 0"}},[a("Upload",{staticStyle:{float:"left"},attrs:{"show-upload-list":!1,headers:t.UploadHeaders,"on-success":t.ImportTaskSuccess,action:t.BaseUrl+"/api/Task/Import?dir="}},[a("Button",{attrs:{type:"primary",size:"small",icon:"md-cloud-upload"}},[t._v("导入")])],1),t._v("\n     \n    "),a("Button",{attrs:{type:"primary",size:"small"},on:{click:t.exportTask}},[a("Icon",{attrs:{type:"md-cloud-download"}}),t._v(" 导出\n    ")],1),t._v("\n     \n    "),a("Button",{attrs:{type:"primary",size:"small"},on:{click:function(a){return t.editTask(-1)}}},[a("Icon",{attrs:{type:"md-add"}}),t._v(" 添加\n    ")],1),t._v("\n     \n    "),a("Button",{attrs:{type:"primary",size:"small"},on:{click:function(a){return t.enableSelection()}}},[a("Icon",{attrs:{type:"md-cog"}}),t._v(" 启用\n    ")],1),t._v("\n     \n    "),a("Button",{attrs:{type:"warning",size:"small"},on:{click:function(a){return t.disableSelection()}}},[a("Icon",{attrs:{type:"md-cog"}}),t._v(" 禁用\n    ")],1),t._v("\n     \n    "),a("Button",{attrs:{type:"error",size:"small"},on:{click:function(a){return t.deleteSelection()}}},[a("Icon",{attrs:{type:"ios-trash"}}),t._v(" 删除\n    ")],1)],1),a("Table",{ref:"EnvTable",attrs:{stripe:"",border:"",columns:t.TaskColumns,data:t.CommadnData}}),a("div",{staticStyle:{margin:"10px",overflow:"hidden"}},[a("div",{staticStyle:{float:"right"}},[a("Page",{attrs:{total:t.PageInfo.Total,current:t.QueryForm.PageIndex,"page-size":t.QueryForm.PageSize,"page-size-opts":t.PageInfo.PageSizeOpts,"show-sizer":!0,"show-total":!0},on:{"on-page-size-change":t.pageSizeChange,"on-change":t.LoadTask}})],1)]),a("Modal",{attrs:{"mask-closable":!1,title:t.EditTaskModal.Title,width:800},model:{value:t.EditTaskModal.Show,callback:function(a){t.$set(t.EditTaskModal,"Show",a)},expression:"EditTaskModal.Show"}},[a("Tabs",{attrs:{value:"PrmaryTask"}},[a("TabPane",{attrs:{label:"主任务",name:"PrmaryTask"}},[a("Form",{ref:"editTaskForm",attrs:{model:t.EditTaskModal.Data,rules:t.ruleValidate,"label-width":80}},[a("Row",[a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"任务名称",prop:"Name"}},[a("Input",{model:{value:t.EditTaskModal.Data.Name,callback:function(a){t.$set(t.EditTaskModal.Data,"Name",a)},expression:"EditTaskModal.Data.Name"}})],1)],1),a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"执行脚本",prop:"FileName"}},[a("Input",{model:{value:t.EditTaskModal.Data.FileName,callback:function(a){t.$set(t.EditTaskModal.Data,"FileName",a)},expression:"EditTaskModal.Data.FileName"}},[a("Upload",{attrs:{slot:"append","show-upload-list":!1,"on-success":t.onSuccess,action:t.BaseUrl+"/api/Upload/scripts?dir="+t.FileNameDir,headers:t.UploadHeaders},slot:"append"},[a("i-button",{attrs:{icon:"ios-cloud-upload-outline"}},[t._v("上传脚本")])],1)],1)],1)],1)],1),a("Row",[a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"触发指令"}},[a("Input",{attrs:{placeholder:"支持正则表达式"},model:{value:t.EditTaskModal.Data.Command,callback:function(a){t.$set(t.EditTaskModal.Data,"Command",a)},expression:"EditTaskModal.Data.Command"}},[a("Checkbox",{attrs:{slot:"append"},slot:"append",model:{value:t.EditTaskModal.Data.EnableRegex,callback:function(a){t.$set(t.EditTaskModal.Data,"EnableRegex",a)},expression:"EditTaskModal.Data.EnableRegex"}},[t._v("正则")])],1)],1)],1),a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"指令变量"}},[a("Input",{attrs:{placeholder:"将用户发送的消息赋值到这个环境变量"},model:{value:t.EditTaskModal.Data.CommandEnv,callback:function(a){t.$set(t.EditTaskModal.Data,"CommandEnv",a)},expression:"EditTaskModal.Data.CommandEnv"}})],1)],1)],1),a("Row",[a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"定时执行",prop:"Cron"}},[a("Input",{model:{value:t.EditTaskModal.Data.Cron,callback:function(a){t.$set(t.EditTaskModal.Data,"Cron",a)},expression:"EditTaskModal.Data.Cron"}},[a("Tooltip",{attrs:{slot:"append",content:"表达式http://cron.ciding.cc点击图标直达",placement:"left"},slot:"append"},[a("Button",{attrs:{icon:"md-alert",to:"http://cron.ciding.cc",target:"_blank"}})],1)],1)],1)],1),a("Col",{attrs:{span:"12"}},[a("FormItem",{attrs:{label:"日限制"}},[a("Input",{attrs:{type:"number",placeholder:"用户当日执行限制"},model:{value:t.EditTaskModal.Data.DayLimit,callback:function(a){t.$set(t.EditTaskModal.Data,"DayLimit",a)},expression:"EditTaskModal.Data.DayLimit"}})],1)],1)],1),a("Row",[a("Col",{attrs:{span:"24"}},[a("FormItem",{attrs:{label:"指令分组",prop:"TaskGroupId"}},[a("Select",{attrs:{clearable:""},model:{value:t.EditTaskModal.Data.TaskGroupId,callback:function(a){t.$set(t.EditTaskModal.Data,"TaskGroupId",a)},expression:"EditTaskModal.Data.TaskGroupId"}},t._l(t.taskGroups,(function(e){return a("Option",{key:e.Id,attrs:{value:e.Id}},[t._v("\n                  "+t._s(e.Name)+"\n                ")])})),1)],1)],1)],1),a("Row",[a("Col",{attrs:{span:"24"}},[a("FormItem",{attrs:{label:"通讯方式",prop:"CommunicationType"}},[a("Select",{attrs:{clearable:"",multiple:""},model:{value:t.EditTaskModal.Data.CommunicationTypeList,callback:function(a){t.$set(t.EditTaskModal.Data,"CommunicationTypeList",a)},expression:"EditTaskModal.Data.CommunicationTypeList"}},t._l(t.AllEnums.CommunicationType,(function(e){return a("Option",{key:e.Value,attrs:{value:e.Value}},[t._v("\n                  "+t._s(e.Key)+"\n                ")])})),1)],1)],1)],1),a("Row",[a("Col",{attrs:{span:"12"}},[t.EditTaskModal.Data.EnableConc?a("FormItem",{attrs:{label:"并发变量",prop:"ConcEnvName"}},[a("Input",{model:{value:t.EditTaskModal.Data.ConcEnvName,callback:function(a){t.$set(t.EditTaskModal.Data,"ConcEnvName",a)},expression:"EditTaskModal.Data.ConcEnvName"}})],1):t._e()],1),a("Col",{attrs:{span:"12"}},[t.EditTaskModal.Data.EnableConc?a("FormItem",{attrs:{label:"并发数量"}},[a("Input",{attrs:{type:"number",placeholder:""},model:{value:t.EditTaskModal.Data.ConcCount,callback:function(a){t.$set(t.EditTaskModal.Data,"ConcCount",a)},expression:"EditTaskModal.Data.ConcCount"}},[a("Tooltip",{attrs:{slot:"append","max-width":"400",content:"建议小于CPU线程数，填0则默认限制CPU线程数2倍",placement:"left"},slot:"append"},[a("Button",{attrs:{icon:"md-alert"}})],1)],1)],1):t._e()],1)],1),a("FormItem",{attrs:{label:"开始通知"}},[a("Input",{attrs:{type:"textarea",placeholder:"任务开始前通知"},model:{value:t.EditTaskModal.Data.TaskStartNotify,callback:function(a){t.$set(t.EditTaskModal.Data,"TaskStartNotify",a)},expression:"EditTaskModal.Data.TaskStartNotify"}})],1),a("FormItem",{attrs:{label:"结束通知"}},[a("Input",{attrs:{type:"textarea",placeholder:"任务结束后通知"},model:{value:t.EditTaskModal.Data.TaskEndNotify,callback:function(a){t.$set(t.EditTaskModal.Data,"TaskEndNotify",a)},expression:"EditTaskModal.Data.TaskEndNotify"}})],1),a("FormItem",{attrs:{label:"备注"}},[a("Input",{attrs:{type:"textarea"},model:{value:t.EditTaskModal.Data.Remark,callback:function(a){t.$set(t.EditTaskModal.Data,"Remark",a)},expression:"EditTaskModal.Data.Remark"}})],1),a("FormItem",{attrs:{label:"开关选项"}},[a("Checkbox",{model:{value:t.EditTaskModal.Data.Enable,callback:function(a){t.$set(t.EditTaskModal.Data,"Enable",a)},expression:"EditTaskModal.Data.Enable"}},[t._v("\n              启用\n              "),a("Tooltip",{attrs:{content:"全局开关",placement:"top"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{model:{value:t.EditTaskModal.Data.Revocation,callback:function(a){t.$set(t.EditTaskModal.Data,"Revocation",a)},expression:"EditTaskModal.Data.Revocation"}},[t._v("\n              撤回消息\n              "),a("Tooltip",{attrs:{content:"触发指令后撤回群消息，仅QQ消息支持",placement:"top"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{model:{value:t.EditTaskModal.Data.EnablePush,callback:function(a){t.$set(t.EditTaskModal.Data,"EnablePush",a)},expression:"EditTaskModal.Data.EnablePush"}},[t._v("\n              消息推送"),a("Tooltip",{attrs:{content:"允许脚本发送通知消息",placement:"top"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{model:{value:t.EditTaskModal.Data.PushGroup,callback:function(a){t.$set(t.EditTaskModal.Data,"PushGroup",a)},expression:"EditTaskModal.Data.PushGroup"}},[t._v("\n              群通知"),a("Tooltip",{attrs:{content:"QQ选项，小心群风控",placement:"top"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{directives:[{name:"show",rawName:"v-show",value:!1,expression:"false"}],model:{value:t.EditTaskModal.Data.EnableLog,callback:function(a){t.$set(t.EditTaskModal.Data,"EnableLog",a)},expression:"EditTaskModal.Data.EnableLog"}},[t._v("记录日志")]),a("Checkbox",{model:{value:t.EditTaskModal.Data.Manager,callback:function(a){t.$set(t.EditTaskModal.Data,"Manager",a)},expression:"EditTaskModal.Data.Manager"}},[t._v("\n              管理员"),a("Tooltip",{attrs:{content:"只允许管理员触发",placement:"top"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{model:{value:t.EditTaskModal.Data.TextToPicture,callback:function(a){t.$set(t.EditTaskModal.Data,"TextToPicture",a)},expression:"EditTaskModal.Data.TextToPicture"}},[t._v("\n              文字转图"),a("Tooltip",{attrs:{content:"消耗更多计算资源，需要从系统设置中配置服务地址。",placement:"top","max-width":"400"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1),a("Checkbox",{model:{value:t.EditTaskModal.Data.EnableProxy,callback:function(a){t.$set(t.EditTaskModal.Data,"EnableProxy",a)},expression:"EditTaskModal.Data.EnableProxy"}},[t._v("\n              代理"),a("Tooltip",{attrs:{content:"仅支持使用quantum.js 中 api 方法请求接口的脚本",placement:"top","max-width":"400"}},[a("Icon",{attrs:{type:"ios-alert"}})],1)],1)],1)],1)],1),t.EditTaskModal.Data.Command?a("TabPane",{attrs:{label:"子任务",name:"name2"}},[a("div",{staticStyle:{"margin-bottom":"10px"}},[a("Button",{attrs:{icon:"md-add",type:"primary"},on:{click:function(a){return t.AddTaskSub("")}}},[t._v("\n            添加\n          ")])],1),t.EditTaskModal.Data.TaskSubs&&t.EditTaskModal.Data.TaskSubs.length>0?a("Collapse",{attrs:{accordion:""},model:{value:t.SelectSubTask,callback:function(a){t.SelectSubTask=a},expression:"SelectSubTask"}},t._l(t.EditTaskModal.Data.TaskSubs,(function(e,n){return a("Panel",{key:e.Id,attrs:{name:"sub"+(n+1)}},[t._v("\n            "+t._s(e.Name)+"\n            "),a("Form",{ref:"subForm"+(n+1),refInFor:!0,attrs:{slot:"content",model:e,rules:t.SubRuleValidate,"label-width":80},slot:"content"},[a("FormItem",{attrs:{label:"任务名称",prop:"Name"}},[a("Input",{model:{value:e.Name,callback:function(a){t.$set(e,"Name",a)},expression:"sub.Name"}})],1),a("FormItem",{attrs:{label:"触发指令",prop:"Command"}},[a("Input",{attrs:{placeholder:"支持正则表达式"},model:{value:e.Command,callback:function(a){t.$set(e,"Command",a)},expression:"sub.Command"}},[a("Checkbox",{attrs:{slot:"append"},slot:"append",model:{value:e.EnableRegex,callback:function(a){t.$set(e,"EnableRegex",a)},expression:"sub.EnableRegex"}},[t._v("正则")])],1)],1),a("FormItem",{attrs:{label:"撤回消息"}},[a("Checkbox",{model:{value:e.Revocation,callback:function(a){t.$set(e,"Revocation",a)},expression:"sub.Revocation"}})],1),a("FormItem",{attrs:{label:"任务序号",prop:"Sort"}},[a("Input",{attrs:{type:"number",placeholder:"子任务按顺序执行"},model:{value:e.Sort,callback:function(a){t.$set(e,"Sort",a)},expression:"sub.Sort"}})],1),a("FormItem",{attrs:{label:"指令变量",prop:"CommandEnv"}},[a("Input",{attrs:{placeholder:"如用户发送的消息与指令匹配，则将用户发送的消息赋值到这个环境变量。"},model:{value:e.CommandEnv,callback:function(a){t.$set(e,"CommandEnv",a)},expression:"sub.CommandEnv"}})],1),a("FormItem",{attrs:{label:"备注"}},[a("Input",{attrs:{type:"textarea"},model:{value:e.Remark,callback:function(a){t.$set(e,"Remark",a)},expression:"sub.Remark"}})],1),a("FormItem",[a("Button",{staticStyle:{float:"right"},attrs:{type:"error",icon:"md-trash"},on:{click:function(a){return t.DeleteTaskSub(n)}}},[t._v("\n                  删除子任务\n                ")])],1)],1)],1)})),1):t._e()],1):t._e()],1),a("div",{attrs:{slot:"footer"},slot:"footer"},[a("Button",{staticStyle:{"margin-left":"8px"},on:{click:function(a){return t.handleCancel()}}},[t._v("取消")]),a("Button",{attrs:{type:"primary"},on:{click:function(a){return t.handleSubmit("editTaskForm")}}},[t._v("\n        提交\n      ")])],1)],1),a("Modal",{attrs:{"footer-hide":!0,"mask-closable":!1,title:t.EditScriptModal.Title,fullscreen:!0},model:{value:t.EditScriptModal.Show,callback:function(a){t.$set(t.EditScriptModal,"Show",a)},expression:"EditScriptModal.Show"}},[a("quantum-scripts",{ref:"taskEditScripts",on:{"save-success":t.onEditScriptSave}})],1)],1)},o=[],s=(e("28a5"),e("7f7f"),e("ac6a"),e("96cf"),e("1da1")),i=e("b199"),l=e("7087"),r=e("f121"),c=e("0f37"),d=function(){var t=this,a=t._self._c;return a("div",{staticStyle:{height:"100%"}},[a("monaco-editor",{staticClass:"editor",attrs:{language:t.getLanguage,readOnly:!1,editorMounted:t.onEditorMounted},model:{value:t.content,callback:function(a){t.content=a},expression:"content"}}),a("footer",[a("Button",{staticClass:"saveBtn",attrs:{type:"primary"},on:{click:t.saveFile}},[t._v("保存")])],1)],1)},u=[],m=e("2b77"),p={name:"quantum-scripts",data:function(){return{content:"",SelectAllQL:!0,QLPanels:[],Files:[],Languages:{".py":"python",".js":"javascript",".json":"json",".sh":"shell",".ts":"typescript"},ModalEdit:!1,FileName:""}},model:{event:"save-success"},computed:{getLanguage:function(){if(this.FileName){var t=this.FileName.split(".");return this.Languages["."+t[t.length-1]]}return"javascript"}},components:{MonacoEditor:m["a"]},methods:{getFile:function(){var t=this;Object(i["f"])({fileName:this.FileName}).then((function(a){t.content=a}))},saveFile:function(){var t=this;this.$Modal.confirm({title:"脚本编辑确认",content:"确认保存修改的脚本内容？",okText:"确定",cancelText:"取消",closable:!0,onOk:function(){Object(i["j"])({path:t.FileName,content:t.content}).then((function(a){t.$Notice.success({desc:"修改脚本文件成功"}),t.$emit("save-success")})).catch((function(a){t.$Notice.error({title:"保存脚本文件失败",desc:a})}))}})},onEditorMounted:function(t,a){window.editor=t,window.monaco=a}},mounted:function(){}},k=p,T=(e("1a87"),e("2877")),f=Object(T["a"])(k,d,u,!1,null,"0bdfa448",null),h=f.exports,b={data:function(){var t=this,a=function(){var a=Object(s["a"])(regeneratorRuntime.mark((function a(e,n,o){return regeneratorRuntime.wrap((function(a){while(1)switch(a.prev=a.next){case 0:if(!t.EditTaskModal.Data.EnableConc||n&&!(n.length<1)){a.next=3;break}return t.changeAgainFlag=2,a.abrupt("return",o(new Error("您已开启并发，请填写并发变量")));case 3:o();case 4:case"end":return a.stop()}}),a)})));return function(t,e,n){return a.apply(this,arguments)}}();return{taskGroups:[],EditScriptModal:{Show:!1,title:"修改脚本"},SelectSubTask:"sub1",BaseUrl:"",TaskColumns:[{type:"selection",width:60,align:"center",fixed:"left"},{title:"任务名称",key:"Name",resizable:!0,width:160,fixed:"left"},{title:"分组名称",resizable:!0,key:"TaskGroupName",minWidth:150},{title:"触发指令",key:"Command",minWidth:240},{title:"执行脚本",resizable:!0,key:"FileName",minWidth:280,render:function(a,e){return a("div",[a("a",{on:{click:function(){t.eidtScripts(e.row)}}},e.row.FileName)])}},{title:"定时执行",key:"Cron",resizable:!0,width:120},{title:"状态",resizable:!0,align:"center",key:"Enable",width:80,render:function(t,a){return t("div",[t("span",{style:{color:a.row.Enable?"green":"red"}},a.row.Enable?"启用":"禁用")])}},{title:"消息推送",key:"EnablePush",resizable:!0,align:"center",width:90,render:function(t,a){return t("div",[t("span",{style:{color:a.row.EnablePush?"green":"red"}},a.row.EnablePush?"是":"否")])}},{title:"代理",resizable:!0,key:"EnablePush",width:80,align:"center",render:function(t,a){return t("div",[t("span",{style:{color:a.row.EnableProxy?"green":"red"}},a.row.EnableProxy?"启用":"禁用")])}},{title:"管理员",key:"Manager",width:80,align:"center",resizable:!0,render:function(t,a){return t("div",[t("span",{style:{color:a.row.Manager?"green":"red"}},a.row.Manager?"是":"否")])}},{title:"子任务",key:"TaskSubsLength",width:90,align:"center",render:function(t,a){return t("div",[t("span",a.row.TaskSubs.length)])}},{title:"操作",key:"action",width:100,resizable:!0,align:"center",fixed:"right",render:function(a,e){var n=[];return n.push(a("i",{class:"fa fa-play",style:{color:"green"},on:{click:function(){t.execTask([e.row.Id])}}})),n.push(a("i",{class:"fa fa-pencil",style:{color:"#ff6a00"},on:{click:function(){t.editTask(e.index)}}})),n.push(a("i",{class:"fa fa-trash",style:{color:"#f00"},on:{click:function(){t.delete([e.row.Id])}}})),a("div",n)}}],FileNameDir:"quantum",UploadHeaders:{Authorization:""},QueryForm:{PageIndex:1,PageSize:15,Enable:void 0},PageInfo:{Total:0,PageSizeOpts:[15,50,100]},AllEnums:{},QLPanels:[],CommadnData:[],EditTaskModal:{Show:!1,Title:"添加脚本指令",Data:{Enable:!0,EnableAll:!1,EnablePush:!1,EnableLog:!0,EnableProxy:!1,FileName:"",TaskGroupId:void 0,CommunicationTypeList:[]}},onEditScriptSave:function(){},SelectAllQL:!0,ruleValidate:{Name:[{required:!0,message:"任务不能为空",trigger:"blur"}],FileName:[{required:!0,message:"执行脚本不能为空",trigger:"blur"}],ConcEnvName:[{validator:a,trigger:"blur"}]},SubRuleValidate:{Name:[{required:!0,message:"任务不能为空",trigger:"blur"}],Command:[{required:!0,message:"触发指令不能为空",trigger:"blur"}],CommandEnv:[{required:!0,message:"指令环境变量不能为空",trigger:"blur"}],Sort:[{required:!0,message:"执行序号不能为空"}]}}},components:{QuantumScripts:h},methods:{exportTask:function(){window.open(this.BaseUrl+"/api/Task/Export?key="+(this.QueryForm.Key||"")+"&TaskGroupId="+(this.QueryForm.TaskGroupId||"")+"&Enable="+(void 0!=this.QueryForm.Enable?this.QueryForm.Enable:""))},disableSelection:function(){var t=this,a=[],e=this.$refs.EnvTable.getSelection();null!=e&&0!=e.length?(e.forEach((function(t){a.push(t.Id)})),Object(i["c"])(a).then((function(){t.LoadTask()}))):this.$Message.warning("请勾选要禁用的脚本指令！")},enableSelection:function(){var t=this,a=[],e=this.$refs.EnvTable.getSelection();null!=e&&0!=e.length?(e.forEach((function(t){a.push(t.Id)})),Object(i["d"])(a).then((function(){t.LoadTask()}))):this.$Message.warning("请勾选要启用的脚本指令！")},deleteSelection:function(){var t=[],a=this.$refs.EnvTable.getSelection();if(null!=a&&0!=a.length){for(var e=0;e<a.length;e++){var n=a[e];t.push(n.Id)}this.delete(t)}else this.$Message.warning("请勾选要删除的脚本指令！")},taskGroupSelectChange:function(){this.LoadTask()},eidtScripts:function(t){this.EditScriptModal.Show=!0,this.EditScriptModal.Title="修改".concat(t.Name,"脚本"),this.$refs.taskEditScripts.ModalEdit=!0,this.$refs.taskEditScripts.FileName=t.FileName,this.$refs.taskEditScripts.getFile()},onSuccess:function(t,a,e){this.EditTaskModal.Data.FileName="".concat(a.name)},execTask:function(t){this.$Modal.confirm({title:"执行确认",content:"确定执行选择的任务吗?",okText:"确认",cancelText:"取消",closable:!0,onOk:function(){var a=this;Object(i["e"])(t).then((function(t){a.$Message.success("执行请求成功!")}))}})},AddTaskSub:function(t){null==this.EditTaskModal.Data.TaskSubs&&(this.EditTaskModal.Data.TaskSubs=[]),this.EditTaskModal.Data.TaskSubs.push({Command:"",Sort:this.EditTaskModal.Data.TaskSubs.length+1,TaskId:t,CommandEnv:"",WaitTime:0,Remark:""}),this.SelectSubTask="sub"+this.EditTaskModal.Data.TaskSubs.length},DeleteTaskSub:function(t){this.EditTaskModal.Data.TaskSubs.splice(t,1)},handleSubmit:function(t){var a=this;this.$refs[t].validate((function(t){if(t){var e=JSON.parse(JSON.stringify(a.EditTaskModal.Data));e.CommunicationTypeList&&e.CommunicationTypeList.length>0&&(e.CommunicationTypes=e.CommunicationTypeList.join(",")),Object(i["h"])(e).then((function(t){t&&(a.$Message.success(a.EditTaskModal.Title+"成功！"),a.EditTaskModal.Show=!1,a.LoadTask())}))}}))},handleCancel:function(){this.EditTaskModal.Show=!1},ImportTaskSuccess:function(t){200===t.Code?(this.$Message.success("成功导入任务".concat(t.Data,"个。")),this.LoadTask(1)):this.$Message.error("导入任务失败："+t.Message),console.log(t)},editTask:function(t){if(t>-1){if(this.EditTaskModal.Title="修改脚本指令",this.EditTaskModal.Data=JSON.parse(JSON.stringify(this.CommadnData[t])),this.CommadnData[t].CommunicationTypes){this.EditTaskModal.Data.CommunicationTypeList=[];for(var a=0;a<this.CommadnData[t].CommunicationTypes.split(",").length;a++){var e=this.CommadnData[t].CommunicationTypes.split(",")[a];this.EditTaskModal.Data.CommunicationTypeList.push(parseInt(e))}}this.EditTaskModal.Data.QLPanelEnvs=[]}else this.initEditTaskModal(),this.EditTaskModal.Title="添加脚本指令";this.EditTaskModal.Show=!0},initEditTaskModal:function(){this.EditTaskModal.Data={Enable:!0,EnableAll:!1,EnablePush:!1,EnableLog:!0,EnableProxy:!1,FileName:"",CommunicationTypeList:[]}},delete:function(t){var a=this;this.$Modal.confirm({title:"删除确认",content:"确定删除选择的[".concat(t.length,"]个脚本指令吗？"),okText:"确认",cancelText:"取消",closable:!0,onOk:function(){Object(i["b"])(t).then((function(t){a.LoadTask(1)}))}})},loadAllEnums:function(){var t=this;Object(c["a"])().then((function(a){t.AllEnums=a}))},pageSizeChange:function(t){this.QueryForm.PageSize=t,this.LoadTask(1)},LoadTask:function(t){var a=this;this.QueryForm.PageIndex=t,Object(i["g"])(this.QueryForm).then((function(t){a.CommadnData=t.Data,a.PageInfo.Total=t.TotalCount}))}},mounted:function(){var t=this;this.UploadHeaders.Authorization=localStorage.getItem("accessToken"),this.BaseUrl=r["a"].baseUrl.dev,this.LoadTask(1),this.loadAllEnums(),Object(l["b"])({PageIndex:1,PageSize:9999}).then((function(a){t.taskGroups=a.Data}))}},E=b,v=(e("1826"),Object(T["a"])(E,n,o,!1,null,null,null));a["default"]=v.exports},7087:function(t,a,e){"use strict";e.d(a,"b",(function(){return o})),e.d(a,"c",(function(){return s})),e.d(a,"a",(function(){return i})),e.d(a,"d",(function(){return l}));var n=e("66df"),o=function(t){return n["a"].request({url:"/api/TaskGroup",params:t,method:"get"})},s=function(t){return n["a"].request({url:"/api/TaskGroup",data:t,method:"post"})},i=function(t){return n["a"].request({url:"/api/TaskGroup",data:t,method:"delete"})},l=function(t){return n["a"].request({url:"/api/TaskGroup/UpdateRepository/"+t,method:"get"})}},"8cbb":function(t,a,e){},"9e72":function(t,a,e){},b199:function(t,a,e){"use strict";e.d(a,"g",(function(){return o})),e.d(a,"h",(function(){return s})),e.d(a,"b",(function(){return i})),e.d(a,"c",(function(){return l})),e.d(a,"d",(function(){return r})),e.d(a,"f",(function(){return c})),e.d(a,"j",(function(){return d})),e.d(a,"e",(function(){return u})),e.d(a,"i",(function(){return m})),e.d(a,"a",(function(){return p}));var n=e("66df"),o=function(t){return n["a"].request({url:"/api/Task",params:t,method:"get"})},s=function(t){return n["a"].request({url:"/api/Task",data:t,method:"post"})},i=function(t){return n["a"].request({url:"/api/Task",data:t,method:"delete"})},l=function(t){return n["a"].request({url:"/api/Task/Disable",data:t,method:"put"})},r=function(t){return n["a"].request({url:"/api/Task/Enable",data:t,method:"put"})},c=function(t){return n["a"].request({url:"/api/Task/Scripts",params:t,method:"get"})},d=function(t){return n["a"].request({url:"/api/Task/Scripts",data:t,method:"put"})},u=function(t){return n["a"].request({url:"/api/Task/ExecTask",data:t,method:"post"})},m=function(){return n["a"].request({url:"/api/Task/script-list",method:"get"})},p=function(t){return n["a"].request({url:"/api/Task/delete-scripts?fileName="+t,method:"delete"})}}}]);