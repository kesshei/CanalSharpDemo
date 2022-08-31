# .NET 6  Mysql Canal (CDC 增量同步,捕获变更数据)
之前业务需要捕捉到业务数据增量部分，并对其进行宽表处理，这也是其中的一个技术方案，方案主要是用了CDC的技术。

CDC 全称是 Change Data Capture，捕获变更数据，是一个比较广泛的概念，只要是能够捕获所有数据的变化，比如数据库捕获完整的变更日志记录增、删、改等，都可以称为 CDC。该功能被广泛应用于数据同步、更新缓存、微服务间同步数据等场景。

而 Canal 是阿里巴巴旗下的一个 CDC中间件，使其成为Mysql的从机来接收实时的数据修改事件。

![](https://tupian.wanmeisys.com/markdown/1661944301804-081ec3a9-48ca-4194-bd80-26b13ca7cc49.png)

>整体大致架构：Mysql -> Canal Server(Docker)  ->  C# BLL  (Canal Client)

整体大概是这样的架构。
## 确定Mysql是否开启BinLog
如果没有开启，是捕获不到日志的。
```
show variables like 'log_bin';  -- binlog 开关
show variables like 'binlog_format';  -- binlog格式 我这边是row
```
![](https://tupian.wanmeisys.com/markdown/1661944535805-18a82a3e-2a40-4e9b-82cb-644eb778f5c0.png)

![](https://tupian.wanmeisys.com/markdown/1661944614364-3e9dbfc4-0146-401c-824a-96ec4cfde592.png)

这样就可以下一步了。
## Canal服务安装
先安装Docker
```csharp
docker pull canal/canal-server:latest
```

大致启动命令如下:
```csharp
命令如下
Usage:
  run.sh [CONFIG]
example 1 :
  run.sh -e canal.instance.master.address=127.0.0.1:3306 \
         -e canal.instance.dbUsername=canal \
         -e canal.instance.dbPassword=canal \
         -e canal.instance.connectionCharset=UTF-8 \
         -e canal.instance.tsdb.enable=true \
         -e canal.instance.gtidon=false \
         -e canal.instance.filter.regex=.*\\\..*
example 2 :
  run.sh -e canal.admin.manager=127.0.0.1:8089 \
         -e canal.admin.port=11110 \
         -e canal.admin.user=admin \
         -e canal.admin.passwd=4ACFE3202A5FF5CF467898FC58AAB1D615029441
```
我这边自己处理后的命令
```csharp
docker run -d -it -h --name=canal-server ^
-p 11110:11110 -p 11111:11111 -p 11112:11112 -p 9100:9100 -m 4096m ^
-e canal.instance.master.address=192.168.1.8:3306 ^
-e canal.instance.dbUsername=root ^
-e canal.instance.dbPassword=123456 ^
-e canal.instance.connectionCharset=UTF-8 ^
-e canal.instance.tsdb.enable=true ^
-e canal.instance.gtidon=false ^
-e canal.instance.filter.regex=.*\\\..* ^
canal/canal-server
```


### Windows使用Docker Desktop出现exit 139错误
docker linux 内核7以下会出现此类问题

建议直接升级内核或者 采用 如下配置

创建或修改 C:\Users\(用户名)\.wslconfig，里面写入

```csharp
[wsl2]
kernelCommandLine = vsyscall=emulate
```
然后，重启电脑主机即可。

## CanalSharp （canal client）客户端
CanalSharp 阿里云的解决方案，需要两部分

Canal  服务端要和Mysql 连在一起（目前我是用docker部署的服务）

另外一部分就是 CanalSharp 单独的客户端服务(.Net 6 服务)

CanalSharp文档 可以参考：https://canalsharp.azurewebsites.net/zh/

而CanalSharp 就可以直接 Nuget 安装

另外一个包是日志，不需要的话，可以不需要，但是，我这个Demo还是需要的
```csharp
Install-Package CanalSharp -Version 1.1.0
Install-Package Microsoft.Extensions.Logging.Console -Version 6.0.0-preview.6.21352.12
```

###  具体的示例
一个简单的数据库表结构，作为演示用。
```
CREATE TABLE `NewTable` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `name` varchar(100) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```
业务代码

```csharp
class Program
{
    private static ILogger _logger;
    static async Task Main(string[] args)
    {
        await SimpleClusterConn();
        Console.WriteLine("同步结束!");
        Console.ReadLine();
    }

    static async Task SimpleClusterConn()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddFilter("Microsoft", LogLevel.Debug)
                .AddFilter("System", LogLevel.Information)
                .AddConsole();
        });
        _logger = loggerFactory.CreateLogger<Program>();
        var conn = new SimpleCanalConnection(new SimpleCanalOptions("127.0.0.1", 11111, "12349"), loggerFactory.CreateLogger<SimpleCanalConnection>());
        await conn.ConnectAsync();
        await conn.SubscribeAsync();
        await conn.RollbackAsync(0);
        while (true)
        {
            var msg = await conn.GetAsync(1024);
            PrintEntry(msg.Entries);
            await Task.Delay(300);
        }
    }

    private static void PrintEntry(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.EntryType == EntryType.Transactionbegin || entry.EntryType == EntryType.Transactionend)
            {
                continue;
            }

            RowChange rowChange = null;

            try
            {
                rowChange = RowChange.Parser.ParseFrom(entry.StoreValue);
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
            }

            if (rowChange != null)
            {
                EventType eventType = rowChange.EventType;

                _logger.LogInformation(
                    $"================> binlog[{entry.Header.LogfileName}:{entry.Header.LogfileOffset}] , name[{entry.Header.SchemaName},{entry.Header.TableName}] , eventType :{eventType}");

                foreach (var rowData in rowChange.RowDatas)
                {
                    if (eventType == EventType.Delete)
                    {
                        PrintColumn(rowData.BeforeColumns.ToList());
                    }
                    else if (eventType == EventType.Insert)
                    {
                        PrintColumn(rowData.AfterColumns.ToList());
                    }
                    else
                    {
                        _logger.LogInformation("-------> before");
                        PrintColumn(rowData.BeforeColumns.ToList());
                        _logger.LogInformation("-------> after");
                        PrintColumn(rowData.AfterColumns.ToList());
                    }
                }
            }
        }
    }

    private static void PrintColumn(List<Column> columns)
    {
        foreach (var column in columns)
        {
            Console.WriteLine($"{column.Name} ： {column.Value}  update=  {column.Updated}");
        }
    }
}
```
启动后，如下图所示：

![](https://tupian.wanmeisys.com/markdown/1661945087545-0d6a447f-39a5-4e47-a6fe-ae8a89f5d418.png)

提示订阅成功。

### 数据库表操作 
1. 插入

![](https://tupian.wanmeisys.com/markdown/1661945424484-19e523dd-568f-4073-8b03-20c015851bd8.png)

2. 更新
 
![](https://tupian.wanmeisys.com/markdown/1661945457776-ac666090-6d43-49fb-8aed-798b4dfabca9.png)

3. 删除

![](https://tupian.wanmeisys.com/markdown/1661945506835-52182589-f878-48c1-bfa3-65b94ce56653.png)

至此，插入，更新，删除的信息都有了。可以根据这些信息，对业务表进行监控等行为来提升业务功能。

# 总结
秋高气爽，终于不用那么热了。

CDC功能总得来说是不可缺的，特别是跟大数据相关的业务，这个步骤总是不可少，无非是别人做和自己来做这件事情，而CDC做的好的，也就阿里了。

用着有问题，就上git上问问，实在不行改改源码。

## 阅
一键三连呦！，感谢大佬的支持，您的支持就是我的动力!


## 代码地址
https://github.com/kesshei/CanalSharpDemo.git

https://gitee.com/kesshei/CanalSharpDemo.git