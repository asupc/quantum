(window["webpackJsonp"]=window["webpackJsonp"]||[]).push([["chunk-16e78051"],{"0f37":function(e,t,o){"use strict";o.d(t,"a",(function(){return a}));var n=o("66df"),a=function(){return n["a"].request({url:"/api/Enum",method:"get"})}},1961:function(e,t,o){},"1a1c":function(e,t,o){"use strict";o.r(t);var n=function(){var e=this,t=e._self._c;return t("div",[t("div",[t("Form",{attrs:{"label-width":80},nativeOn:{keydown:function(t){return!t.type.indexOf("key")&&e._k(t.keyCode,"enter",13,t.key,"Enter")?null:(t.preventDefault(),e.loadLogs(1))}}},[t("Row",[t("Col",{attrs:{span:"4"}},[t("FormItem",{attrs:{label:"搜索"}},[t("Input",{attrs:{placeholder:"输入搜索关键字"},model:{value:e.QueryForm.Key,callback:function(t){e.$set(e.QueryForm,"Key",t)},expression:"QueryForm.Key"}})],1)],1),t("Col",{attrs:{span:"4"}},[t("FormItem",{attrs:{label:"日志类型"}},[t("Select",{attrs:{clearable:""},model:{value:e.QueryForm.LogType,callback:function(t){e.$set(e.QueryForm,"LogType",t)},expression:"QueryForm.LogType"}},e._l(e.AllEnums.LogType,(function(o){return t("Option",{key:o.Value,attrs:{value:o.Value}},[e._v("\n              "+e._s(o.Key)+"\n            ")])})),1)],1)],1),t("Col",{attrs:{span:"6"}},[t("FormItem",{attrs:{label:"时间"}},[t("DatePicker",{attrs:{format:"yyyy/MM/dd HH:mm",type:"daterange",placement:"bottom-end",placeholder:"选择日志时间"},model:{value:e.QueryForm.DateRange,callback:function(t){e.$set(e.QueryForm,"DateRange",t)},expression:"QueryForm.DateRange"}})],1)],1),e._v("\n         \n        "),t("Button",{attrs:{type:"info"},on:{click:function(t){return e.loadLogs(1)}}},[t("Icon",{attrs:{type:"md-search"}}),e._v(" 搜索\n        ")],1)],1)],1)],1),t("div",{staticStyle:{margin:"20px 0"}},[t("Button",{attrs:{type:"error",size:"small"},on:{click:e.deletLogs}},[t("Icon",{attrs:{type:"md-trash"}}),e._v(" 删除\n    ")],1),e._v("\n     \n    "),t("Button",{attrs:{type:"error",size:"small"},on:{click:e.clearLogs}},[t("Icon",{attrs:{type:"md-trash"}}),e._v(" 清空日志\n    ")],1)],1),t("Table",{ref:"LogTable",attrs:{stripe:"",border:"",columns:e.LogsColumns,data:e.LogsData}}),t("div",{staticStyle:{margin:"10px",overflow:"hidden"}},[t("div",{staticStyle:{float:"right"}},[t("Page",{attrs:{total:e.PageInfo.Total,current:e.QueryForm.PageIndex,"page-size":e.QueryForm.PageSize,"show-sizer":!0,"page-size-opts":e.PageInfo.PageSizeOpts,"show-total":!0},on:{"on-page-size-change":e.pageSizeChange,"on-change":e.loadLogs}})],1)]),t("div",{staticClass:"logModal"},[t("Modal",{attrs:{"mask-closable":!1,title:e.LogModal.Title,width:800,styles:{top:"20px"}},model:{value:e.LogModal.Show,callback:function(t){e.$set(e.LogModal,"Show",t)},expression:"LogModal.Show"}},[t("pre",{staticClass:"log-container"},[e._v(e._s(e.LogModal.LogContent))])])],1)],1)},a=[],r=o("0f37"),l=o("66df"),i=function(e){return l["a"].request({url:"/api/Logs",params:e,method:"get"})},s=function(e){return l["a"].request({url:"/api/Logs",data:e,method:"delete"})},u=function(){return l["a"].request({url:"/api/Logs/Clear",method:"delete"})},c=function(e){return l["a"].request({url:"/api/Logs/details/"+e,method:"get"})},d=o("c276"),g={data:function(){var e=this;return{LogModal:{Show:!1,LogContent:""},LogsColumns:[{type:"selection",width:60,align:"center"},{width:80,type:"index",title:"序号",align:"center"},{title:"标题",width:150,key:"Title",render:function(t,o){return o.row.LogPath?t("div",[t("a",{on:{click:function(){e.logDetails(o.row)}}},o.row.Title)]):t("div",[t("span",o.row.Title)])}},{title:"操作人",key:"Operator",width:180,render:function(t,o){return e.tdTip(t,o,"Operator",!1)}},{title:"说明",key:"Remark",minWidth:150,render:function(t,o){return e.tdTip(t,o,"Remark")}},{title:"时间",key:"CreateTime",width:150},{title:"日志类型",key:"LogType",width:90,render:function(t,o){return t("div",[t("span",Object(d["c"])(e.AllEnums,"LogType",o.row.LogType))])}}],LogsData:[],AllEnums:{},PageInfo:{PageSizeOpts:[10,20,50,100,200,500,1e3],Total:0},QueryForm:{LogType:null,Key:"",StartTime:null,EndTime:null,PageIndex:1,PageSize:20,DateRange:[]}}},methods:{loadLogs:function(e){var t=this;this.QueryForm.PageIndex=e,this.QueryForm.StartTime=this.QueryForm.DateRange[0],this.QueryForm.EndTime=this.QueryForm.DateRange[1],i(this.QueryForm).then((function(e){t.LogsData=e.Data,t.PageInfo.Total=e.TotalCount}))},clearLogs:function(){var e=this;this.$Modal.confirm({title:"清空确认",content:"确定清空所有的日志吗？该操作不可逆。",okText:"确认",cancelText:"取消",closable:!0,onOk:function(){u().then((function(t){e.loadLogs(1),e.$Message.success("日志删除成功！")}))}})},deletLogs:function(){var e=[],t=this.$refs.LogTable.getSelection();if(null!=t&&0!=t.length){for(var o=0;o<t.length;o++){var n=t[o];e.push(n.Id)}var a=this;this.$Modal.confirm({title:"删除确认",content:"确定删除选中的日志吗？该操作不可逆。",okText:"确认",cancelText:"取消",closable:!0,onOk:function(){s(e).then((function(e){a.loadLogs(1),a.$Message.success("日志删除成功！")}))}})}else this.$Message.warning("请选中日志后再操作！")},logDetails:function(e){var t=this;c(e.Id).then((function(o){o&&(t.LogModal.Show=!0,t.LogModal.LogContent=o,t.LogModal.Title=e.Title+"-----详细日志")}))},copy:function(e){var t=this;this.$copyText(e).then((function(e){t.$Message.success("复制成功！")}),(function(e){t.$Message.error("复制失败！")}))},tdTip:function(e,t,o){var n=this,a=!(arguments.length>3&&void 0!==arguments[3])||arguments[3],r=[],l=t.row[o];return r.push(e("a",{style:{display:"inline-block",width:"100%",textIndent:"0px",overflow:"hidden",textOverflow:"ellipsis",whiteSpace:"nowrap"},attrs:{title:l},on:{click:function(){a?(n.LogModal.LogContent=l,n.LogModal.Title="日志详情",n.LogModal.Show=!0):n.copy(l)}}},l)),e("div",{style:{width:"100%"}},r)},pageSizeChange:function(e){this.QueryForm.PageSize=e,console.log(this.QueryForm),this.loadLogs(1)},loadAllEnums:function(){var e=this;Object(r["a"])().then((function(t){e.AllEnums=t}))}},mounted:function(){this.loadAllEnums(),this.loadLogs(1)}},h=g,p=(o("66f6"),o("0c7c")),m=Object(p["a"])(h,n,a,!1,null,"0a82c1d4",null);t["default"]=m.exports},"66f6":function(e,t,o){"use strict";o("1961")}}]);