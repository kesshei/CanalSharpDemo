using CanalSharp.Connections;
using CanalSharp.Protocol;
using Microsoft.Extensions.Logging;

namespace CanalSharpDemo
{
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

                    _logger.LogInformation("----------------------->");
                    _logger.LogInformation($"================> binlog[{entry.Header.LogfileName}:{entry.Header.LogfileOffset}] , name[{entry.Header.SchemaName},{entry.Header.TableName}] , eventType :{eventType}");

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
}