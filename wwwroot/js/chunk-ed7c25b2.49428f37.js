(window["webpackJsonp"]=window["webpackJsonp"]||[]).push([["chunk-ed7c25b2"],{"6f94":function(e,t,a){"use strict";a("bb7d")},"718b":function(e,t,a){"use strict";a.r(t);var r=function(){var e=this,t=e.$createElement,a=e._self._c||t;return a("div",[a("div",[a("Form",{attrs:{"label-width":80},nativeOn:{keydown:function(t){return!t.type.indexOf("key")&&e._k(t.keyCode,"enter",13,t.key,"Enter")?null:(t.preventDefault(),e.loadEnvs(1))}}},[a("Row",[a("Col",{attrs:{span:"4"}},[a("FormItem",{attrs:{label:"搜索"}},[a("Input",{attrs:{placeholder:"输入搜索关键字"},model:{value:e.QueryForm.Key,callback:function(t){e.$set(e.QueryForm,"Key",t)},expression:"QueryForm.Key"}})],1)],1),e._v("\n         \n        "),a("Button",{attrs:{type:"info"},on:{click:function(t){return e.loadUserData(1)}}},[a("Icon",{attrs:{type:"md-search"}}),e._v(" 搜索\n        ")],1)],1)],1)],1),a("div",{staticStyle:{"margin-bottom":"10px"}},[a("Button",{attrs:{type:"error",size:"small"},on:{click:e.deleteSelection}},[a("Icon",{attrs:{type:"md-trash"}}),e._v(" 删除\n    ")],1)],1),a("Table",{ref:"UserTable",attrs:{stripe:"",border:"",columns:e.UserTable.Columns,data:e.UserTable.Datas}}),a("div",{staticStyle:{margin:"10px",overflow:"hidden"}},[a("div",{staticStyle:{float:"right"}},[a("Page",{attrs:{total:e.PageInfo.Total,current:e.QueryForm.PageIndex,"page-size":e.QueryForm.PageSize,"show-sizer":!0,"page-size-opts":e.PageInfo.PageSizeOpts,"show-total":!0},on:{"on-page-size-change":e.pageSizeChange,"on-change":e.loadUserData}})],1)]),a("Modal",{attrs:{"mask-closable":!1,title:e.UserUpdateModal.Title,width:600},model:{value:e.UserUpdateModal.Show,callback:function(t){e.$set(e.UserUpdateModal,"Show",t)},expression:"UserUpdateModal.Show"}},[a("Form",{ref:"EditUserForm",attrs:{model:e.UserUpdateModal.Data,"label-width":80}},[a("FormItem",{attrs:{label:"用户Id"}},[a("Input",{attrs:{disabled:""},model:{value:e.UserUpdateModal.Data.Id,callback:function(t){e.$set(e.UserUpdateModal.Data,"Id",t)},expression:"UserUpdateModal.Data.Id"}})],1),a("FormItem",{attrs:{label:"QQ"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.qq,callback:function(t){e.$set(e.UserUpdateModal.Data,"qq",t)},expression:"UserUpdateModal.Data.qq"}})],1),a("FormItem",{attrs:{label:"QQ昵称"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.qqname,callback:function(t){e.$set(e.UserUpdateModal.Data,"qqname",t)},expression:"UserUpdateModal.Data.qqname"}})],1),a("FormItem",{attrs:{label:"微信ID"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.wxid,callback:function(t){e.$set(e.UserUpdateModal.Data,"wxid",t)},expression:"UserUpdateModal.Data.wxid"}})],1),a("FormItem",{attrs:{label:"微信昵称"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.wxname,callback:function(t){e.$set(e.UserUpdateModal.Data,"wxname",t)},expression:"UserUpdateModal.Data.wxname"}})],1),a("FormItem",{attrs:{label:"wxpusherID"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.wxpusheruserid,callback:function(t){e.$set(e.UserUpdateModal.Data,"wxpusheruserid",t)},expression:"UserUpdateModal.Data.wxpusheruserid"}})],1),a("FormItem",{attrs:{label:"WebId"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.WebId,callback:function(t){e.$set(e.UserUpdateModal.Data,"WebId",t)},expression:"UserUpdateModal.Data.WebId"}})],1),a("FormItem",{attrs:{label:"公众号ID"}},[a("Input",{attrs:{type:"text",disabled:""},model:{value:e.UserUpdateModal.Data.wxmpuserid,callback:function(t){e.$set(e.UserUpdateModal.Data,"wxmpuserid",t)},expression:"UserUpdateModal.Data.wxmpuserid"}})],1),a("FormItem",{attrs:{label:"权重"}},[a("Input",{attrs:{type:"number"},model:{value:e.UserUpdateModal.Data.Weight,callback:function(t){e.$set(e.UserUpdateModal.Data,"Weight",t)},expression:"UserUpdateModal.Data.Weight"}})],1),a("FormItem",{attrs:{label:"积分"}},[a("Input",{attrs:{type:"number"},model:{value:e.UserUpdateModal.Data.MaxEnvCount,callback:function(t){e.$set(e.UserUpdateModal.Data,"MaxEnvCount",t)},expression:"UserUpdateModal.Data.MaxEnvCount"}})],1),a("FormItem",{attrs:{label:"备注"}},[a("Input",{attrs:{type:"text",name:"Remark"},model:{value:e.UserUpdateModal.Data.Remark,callback:function(t){e.$set(e.UserUpdateModal.Data,"Remark",t)},expression:"UserUpdateModal.Data.Remark"}})],1)],1),a("div",{attrs:{slot:"footer"},slot:"footer"},[a("Button",{staticStyle:{"margin-left":"8px"},on:{click:function(t){e.UserUpdateModal.Show=!1}}},[e._v("取消")]),a("Button",{attrs:{type:"primary"},on:{click:function(t){return e.submitUser("EditUserForm")}}},[e._v("提交")])],1)],1)],1)},n=[],s=a("c24f"),l={data:function(){var e=this;return{UserTable:{Columns:[{type:"selection",width:60,align:"center"},{title:"用户ID",key:"Id",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"Id")}},{title:"QQ",key:"qq",Width:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"qq")}},{title:"QQ昵称",key:"qqname",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"qqname")}},{title:"微信ID",key:"wxid",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"wxid")}},{title:"微信昵称",key:"wxname",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"wxname")}},{title:"公众号",key:"wxmpuserid",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"wxmpuserid")}},{title:"WebId",key:"WebId",minWidth:90,ellipsis:!0,render:function(t,a){return e.tdTip(t,a,"WebId")}},{title:"积分",Width:60,key:"MaxEnvCount"},{title:"权重",Width:60,key:"Weight"},{title:"备注",minWidth:120,key:"remark"},{title:"操作",key:"action",width:150,align:"center",render:function(t,a){var r=[];return r.push(t("i",{class:"fa fa-pencil",style:{color:"#ff6a00"},on:{click:function(){e.Update(a.row)}}})),r.push(t("i",{class:"fa fa-trash",style:{color:"#f00"},on:{click:function(){e.delete([a.row.Id])}}})),t("div",r)}}],Datas:[]},QueryForm:{PageSize:50,PageIndex:10,CommunicationType:null,Key:""},PageInfo:{PageSizeOpts:[10,20,50,100,200,500,1e3],Total:0},UserUpdateModal:{Title:"修改用户信息",Show:!1,Data:{}}}},methods:{pageSizeChange:function(e){this.QueryForm.PageSize=e,this.loadUserData(1)},loadUserData:function(e){var t=this;this.QueryForm.PageIndex=e,Object(s["b"])(this.QueryForm).then((function(e){t.UserTable.Datas=e.Data,t.PageInfo.Total=e.TotalCount}))},submitUser:function(){var e=this;Object(s["c"])(this.UserUpdateModal.Data).then((function(t){e.loadUserData(1),e.UserUpdateModal.Show=!1}))},Update:function(e){this.UserUpdateModal.Data=JSON.parse(JSON.stringify(e)),this.UserUpdateModal.Show=!0},tdTip:function(e,t,a){var r=this,n=[],s=t.row[a];return n.push(e("a",{style:{display:"inline-block",width:"100%",textIndent:"0px",overflow:"hidden",textOverflow:"ellipsis",whiteSpace:"nowrap"},attrs:{title:s},on:{click:function(){r.copy(s)}}},s)),e("div",{style:{width:"100%"}},n)},getSelectIds:function(){var e=[],t=this.$refs.UserTable.getSelection();if(null!==t&&0!==t.length){for(var a=0;a<t.length;a++){var r=t[a];e.push(r.Id)}return e}this.$Message.warning("请选中用户后再操作！")},deleteSelection:function(){var e=this.getSelectIds();e.length>0&&this.delete(e)},delete:function(e){var t=this;this.$Modal.confirm({title:"删除确认",content:"确定删除选中的用户？",okText:"是的，老子要删除",cancelText:"算了，不删了",closable:!0,onOk:function(){Object(s["a"])(e).then((function(e){t.loadUserData(1)}))}})},copy:function(e){var t=this;this.$copyText(e).then((function(e){t.$Message.success("复制成功！")}),(function(e){t.$Message.error("复制失败！")}))}},mounted:function(){this.loadUserData(1)}},o=l,i=(a("6f94"),a("2877")),d=Object(i["a"])(o,r,n,!1,null,null,null);t["default"]=d.exports},bb7d:function(e,t,a){}}]);