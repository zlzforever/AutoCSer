﻿using System;
using System.Data.Common;
using System.Data;
using AutoCSer.Extension;
using System.Collections.Generic;
using System.Threading;
using AutoCSer.Metadata;
using MySql.Data.MySqlClient;

namespace AutoCSer.Sql.MySql
{
    /// <summary>
    /// MySql 客户端
    /// </summary>
    public sealed unsafe class Client : Sql.Client
    {
        //grant usage on *.* to xxx_user@127.0.0.1 identified by 'xxx_pwd' with grant option;
        //flush privileges;
        //create database xxx;
        //grant all privileges on xxx.* to xxx_user@127.0.0.1 identified by 'xxx_pwd';
        //flush privileges;
        /// <summary>
        /// SQL客户端操作
        /// </summary>
        /// <param name="connection">SQL连接信息</param>
        [AutoCSer.IOS.Preserve(Conditional = true)]
        internal Client(Connection connection) : base(connection) { }
        /// <summary>
        /// 创建 SQL 连接
        /// </summary>
        /// <param name="connection"></param>
        protected override void createConnection(ref DbConnection connection)
        {
            (connection = new MySqlConnection(Connection.ConnectionString)).Open();
        }
        /// <summary>
        /// 获取SQL命令
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="sql">SQL语句</param>
        /// <returns>SQL命令</returns>
        protected override DbCommand getCommand(DbConnection connection, string sql)
        {
            DbCommand command = new MySqlCommand(sql, new UnionType { Value = connection }.MySqlConnection);
            command.CommandType = CommandType.Text;
            return command;
        }
        /// <summary>
        /// 获取数据适配器
        /// </summary>
        /// <param name="command">SQL命令</param>
        /// <returns>数据适配器</returns>
        protected override DbDataAdapter getAdapter(DbCommand command)
        {
            return new MySqlDataAdapter(new UnionType { Value = command }.MySqlCommand);
        }
        /// <summary>
        /// 最大字符串长度(最大65532字节)
        /// </summary>
        private const int maxStringSize = 65535;
        /// <summary>
        /// 成员信息转换为数据列
        /// </summary>
        /// <param name="type">成员类型</param>
        /// <param name="memberAttribute">SQL成员信息</param>
        /// <returns>数据列</returns>
        internal override Column GetColumn(Type type, MemberAttribute memberAttribute)
        {
            SqlDbType sqlType = SqlDbType.NVarChar;
            int size = maxStringSize;
            Type memberType = memberAttribute.DataType ?? type;
            if (memberType == typeof(string))
            {
                if (memberAttribute.MaxStringLength > 0 && memberAttribute.MaxStringLength <= maxStringSize)
                {
                    if (memberAttribute.IsFixedLength) sqlType = memberAttribute.IsAscii ? SqlDbType.Char : SqlDbType.NChar;
                    else sqlType = memberAttribute.IsAscii ? SqlDbType.VarChar : SqlDbType.NVarChar;
                    size = memberAttribute.MaxStringLength <= maxStringSize ? memberAttribute.MaxStringLength : maxStringSize;
                }
                else if (!memberAttribute.IsFixedLength && memberAttribute.MaxStringLength == -1)
                {
                    sqlType = memberAttribute.IsAscii ? SqlDbType.VarChar : SqlDbType.NVarChar;
                    size = memberAttribute.MaxStringLength <= maxStringSize ? memberAttribute.MaxStringLength : maxStringSize;
                }
                else
                {
                    sqlType = memberAttribute.IsAscii ? SqlDbType.Text : SqlDbType.NText;
                    if (size <= 0) size = int.MaxValue;
                }
            }
            else
            {
                sqlType = memberType.formCSharpType();
                size = sqlType.getSize();
            }
            return new Column
            {
                DbType = sqlType,
                Size = size,
                IsNull = memberAttribute.IsDefaultMember && memberType != typeof(string) ? type.isNull() : memberAttribute.IsNull,
                DefaultValue = memberAttribute.DefaultValue,
                UpdateValue = memberAttribute.UpdateValue
            };
        }
        /// <summary>
        /// 创建索引
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="tableName">表格名称</param>
        /// <param name="columnCollection">索引列集合</param>
        internal override void CreateIndex(DbConnection connection, string tableName, ColumnCollection columnCollection)
        {
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            string sql;
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                sqlStream.WriteNotNull(@"
create index`");
                AppendIndexName(sqlStream, tableName, columnCollection);
                sqlStream.WriteNotNull("`on`");
                sqlStream.WriteNotNull(tableName);
                sqlStream.WriteNotNull("`(");
                bool isNext = false;
                foreach (Column column in columnCollection.Columns)
                {
                    if (isNext) sqlStream.Write(',');
                    sqlStream.Write('`');
                    sqlStream.WriteNotNull(column.Name);
                    sqlStream.Write('`');
                    isNext = true;
                }
                sqlStream.WriteNotNull(");");
                sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            executeNonQuery(connection, sql);
        }
        /// <summary>
        /// 判断表格是否存在
        /// </summary>
        /// <param name="connection">SQL连接</param>
        /// <param name="tableName">表格名称</param>
        /// <returns>表格是否存在</returns>
        private bool isTable(DbConnection connection, string tableName)
        {
            using (DbCommand command = getCommand(connection, "show tables;"))
            using (DbDataReader reader = command.ExecuteReader(CommandBehavior.Default))
            {
                while (reader.Read())
                {
                    if (tableName.equalCaseNotNull((string)reader[0])) return true;
                }
            }
            return false;
        }
        /// <summary>
        /// 根据表格名称获取表格信息
        /// </summary>
        /// <param name="connection">SQL连接</param>
        /// <param name="tableName">表格名称</param>
        /// <returns>表格信息</returns>
        internal override TableColumnCollection GetTable(DbConnection connection, string tableName)
        {
            if (isTable(connection, tableName))
            {
                using (DbCommand command = getCommand(connection, @"describe `" + tableName + @"`;
show index from `" + tableName + @"`;"))
                using (DbDataReader reader = command.ExecuteReader(CommandBehavior.Default))
                {
                    Column identity = null;
                    Dictionary<HashString, Column> columns = DictionaryCreator.CreateHashString<Column>();
                    LeftArray<Column> primaryKeys = default(LeftArray<Column>);
                    Dictionary<HashString, ListArray<IndexColumn>> indexs = null;
                    while (reader.Read())
                    {
                        string key = (string)reader["Key"];
                        object defaultValue = reader["Default"];
                        Column column = new Column
                        {
                            Name = (string)reader["Field"],
                            DefaultValue = defaultValue == DBNull.Value ? null : (string)defaultValue,
                            IsNull = (string)reader["Null"] == "YES",
                        };
                        column.DbType = DbType.FormatDbType((string)reader["Type"], out column.Size);
                        columns.Add(column.Name, column);
                        if (key == "PRI") primaryKeys.Add(column);
                    }
                    if (reader.NextResult())
                    {
                        indexs = DictionaryCreator.CreateHashString<ListArray<IndexColumn>>();
                        ListArray<IndexColumn> indexColumns;
                        while (reader.Read())
                        {
                            string name = (string)reader["Key_name"];
                            IndexColumn indexColumn = new IndexColumn
                            {
                                Column = columns[(string)reader["Column_name"]],
                                Index = (int)(long)reader["Seq_in_index"],
                                IsNull = (string)reader["Null"] == "YES"
                            };
                            HashString nameKey = name;
                            if (!indexs.TryGetValue(nameKey, out indexColumns))
                            {
                                indexs.Add(nameKey, indexColumns = new ListArray<IndexColumn>());
                                indexColumns.Add(indexColumn);
                                indexColumn.Type = (long)reader["Non_unique"] == 0 ? ColumnCollectionType.UniqueIndex : ColumnCollectionType.Index;
                            }
                            else indexColumns.Add(indexColumn);
                        }
                    }
                    return new TableColumnCollection
                    {
                        Columns = new ColumnCollection
                        {
                            Name = tableName,
                            Columns = columns.Values.getArray(),
                            Type = ColumnCollectionType.None
                        },
                        Identity = identity,
                        PrimaryKey = primaryKeys.Length == 0 ? null : new ColumnCollection { Type = ColumnCollectionType.PrimaryKey, Columns = primaryKeys.ToArray() },
                        Indexs = indexs.getArray(index => new ColumnCollection
                        {
                            Name = index.Key.ToString(),
                            Type = index.Value[0].Type,
                            Columns = index.Value.toLeftArray().getSort(value => value.Index).getArray(column => column.Column)
                        })
                    };
                }
            }
            return null;
        }
        /// <summary>
        /// 创建表格
        /// </summary>
        /// <param name="connection">SQL连接</param>
        /// <param name="table">表格信息</param>
        internal override void CreateTable(DbConnection connection, TableColumnCollection table)
        {
            string tableName = table.Columns.Name, sql;
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                sqlStream.WriteNotNull("create table`");
                sqlStream.WriteNotNull(tableName);
                sqlStream.WriteNotNull("`(");
                bool isNext = false;
                foreach (Column column in table.Columns.Columns)
                {
                    if (isNext) sqlStream.Write(',');
                    appendColumn(sqlStream, column);
                    isNext = true;
                }
                ColumnCollection primaryKey = table.PrimaryKey;
                if (primaryKey != null && primaryKey.Columns.length() != 0)
                {
                    isNext = false;
                    sqlStream.WriteNotNull(",primary key(");
                    foreach (Column column in primaryKey.Columns)
                    {
                        if (isNext) sqlStream.Write(',');
                        sqlStream.WriteNotNull(column.Name);
                        isNext = true;
                    }
                    sqlStream.Write(')');
                }
                if (table.Indexs != null)
                {
                    foreach (ColumnCollection columns in table.Indexs)
                    {
                        if (columns != null && columns.Columns.length() != 0)
                        {
                            if (columns.Type == ColumnCollectionType.UniqueIndex) sqlStream.WriteNotNull(@"unique index ");
                            else sqlStream.WriteNotNull(@"
index ");
                            AppendIndexName(sqlStream, tableName, columns);
                            sqlStream.Write('(');
                            isNext = false;
                            foreach (Column column in columns.Columns)
                            {
                                if (isNext) sqlStream.Write(',');
                                sqlStream.Write('`');
                                sqlStream.WriteNotNull(column.Name);
                                sqlStream.Write('`');
                                isNext = true;
                            }
                            sqlStream.Write(')');
                        }
                    }
                }
                sqlStream.WriteNotNull(");");
                sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            executeNonQuery(connection, sql);
        }
        /// <summary>
        /// 写入列信息
        /// </summary>
        /// <param name="sqlStream">SQL语句流</param>
        /// <param name="column">列信息</param>
        private static void appendColumn(CharStream sqlStream, Column column)
        {
            sqlStream.Write('`');
            sqlStream.WriteNotNull(column.Name);
            sqlStream.WriteNotNull("` ");
            if (column.DbType == SqlDbType.Text || column.DbType == SqlDbType.NText)
            {
                if (column.Size <= 65535) sqlStream.WriteNotNull("TEXT");
                else if (column.Size <= 16777215) sqlStream.WriteNotNull("MEDIUMTEXT");
                else sqlStream.WriteNotNull("LONGTEXT");
                sqlStream.WriteNotNull(column.DbType == SqlDbType.NText ? " UNICODE" : " ASCII");
            }
            else
            {
                sqlStream.WriteNotNull(column.DbType.getSqlTypeName());
                if (column.DbType.isStringType() != 0)
                {
                    if (column.Size != int.MaxValue)
                    {
                        sqlStream.Write('(');
                        sqlStream.WriteNotNull(column.Size.toString());
                        sqlStream.Write(')');
                    }
                    sqlStream.WriteNotNull(column.DbType == SqlDbType.NChar || column.DbType == SqlDbType.NVarChar ? " UNICODE" : " ASCII");
                }
            }
            if (column.DefaultValue != null)
            {
                sqlStream.WriteNotNull(" default ");
                sqlStream.WriteNotNull(column.DefaultValue);
            }
            if (!column.IsNull) sqlStream.WriteNotNull(" not null");
            if (!string.IsNullOrEmpty(column.Remark))
            {
                sqlStream.WriteNotNull(" comment '");
                ConstantConverter.Default.Convert(sqlStream, column.Remark);
                sqlStream.Write('\'');
            }
        }
        /// <summary>
        /// 删除列集合
        /// </summary>
        /// <param name="connection">SQL连接</param>
        /// <param name="columnCollection">删除列集合</param>
        internal override void DeleteFields(DbConnection connection, ColumnCollection columnCollection)
        {
            string tableName = columnCollection.Name, sql;
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                foreach (Column column in columnCollection.Columns)
                {
                    sqlStream.WriteNotNull(@"
alter table `");
                    sqlStream.WriteNotNull(tableName);
                    sqlStream.WriteNotNull(@"` drop column ");
                    sqlStream.Write('`');
                    sqlStream.WriteNotNull(column.Name);
                    sqlStream.WriteNotNull("`;");
                }
                sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            executeNonQuery(connection, sql);
        }
        /// <summary>
        /// 新增列集合
        /// </summary>
        /// <param name="connection">SQL连接</param>
        /// <param name="columnCollection">新增列集合</param>
        internal override void AddFields(DbConnection connection, ColumnCollection columnCollection)
        {
            string tableName = columnCollection.Name, sql;
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            bool isUpdateValue = false;
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                foreach (Column column in columnCollection.Columns)
                {
                    sqlStream.WriteNotNull(@"
alter table `");
                    sqlStream.WriteNotNull(tableName);
                    sqlStream.WriteNotNull(@"` add ");
                    if (!column.IsNull && column.DefaultValue == null)
                    {
                        column.DefaultValue = column.DbType.getDefaultValue();
                        if (column.DefaultValue == null) column.IsNull = true;
                    }
                    appendColumn(sqlStream, column);
                    sqlStream.Write(';');
                    if (column.UpdateValue != null) isUpdateValue = true;
                }
                if (isUpdateValue)
                {
                    sqlStream.WriteNotNull(@"
update `");
                    sqlStream.WriteNotNull(tableName);
                    sqlStream.WriteNotNull("` set ");
                    foreach (Column column in columnCollection.Columns)
                    {
                        if (column.UpdateValue != null)
                        {
                            if (!isUpdateValue) sqlStream.Write(',');
                            sqlStream.WriteNotNull(column.Name);
                            sqlStream.Write('=');
                            sqlStream.WriteNotNull(column.UpdateValue);
                            isUpdateValue = false;
                        }
                    }
                    sqlStream.Write(';');
                }
                sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            executeNonQuery(connection, sql);
        }
        /// <summary>
        /// 获取查询信息
        /// </summary>
        /// <typeparam name="valueType">对象类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="createQuery"></param>
        /// <param name="query">查询信息</param>
        /// <returns>对象集合</returns>
        internal override void GetSelectQuery<valueType, modelType>
            (Sql.Table<valueType, modelType> sqlTool, ref CreateSelectQuery<modelType> createQuery, ref SelectQuery<modelType> query)
        {
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                sqlStream.WriteNotNull("select ");
                DataModel.Model<modelType>.GetNames(sqlStream, query.MemberMap);
                sqlStream.WriteNotNull(" from `");
                sqlStream.WriteNotNull(sqlTool.TableName);
                sqlStream.Write('`');
                sqlStream.Write(' ');
                createQuery.WriteWhere(sqlTool, sqlStream, ref query);
                createQuery.WriteOrder(sqlTool, sqlStream, ref query);
                if ((createQuery.GetCount | query.SkipCount) != 0)
                {
                    sqlStream.WriteNotNull(" limit ");
                    AutoCSer.Extension.Number.ToString(query.SkipCount, sqlStream);
                    sqlStream.Write(',');
                    AutoCSer.Extension.Number.ToString(createQuery.GetCount, sqlStream);
                }
                sqlStream.Write(';');
                query.Sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
        }
        /// <summary>
        /// 查询对象
        /// </summary>
        /// <typeparam name="valueType">对象类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="value">匹配成员值</param>
        /// <param name="memberMap">成员位图</param>
        /// <param name="query">查询信息</param>
        internal override void GetByIdentity<valueType, modelType>
            (Sql.Table<valueType, modelType> sqlTool, valueType value, MemberMap<modelType> memberMap, ref GetQuery<modelType> query)
        {
            query.MemberMap = DataModel.Model<modelType>.CopyMemberMap;
            if (memberMap != null && !memberMap.IsDefault) query.MemberMap.And(memberMap);
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                getByIdentity(sqlStream, sqlTool.TableName, value, memberMap);
                query.Sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
        }
        /// <summary>
        /// 查询对象
        /// </summary>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlStream"></param>
        /// <param name="tableName"></param>
        /// <param name="value"></param>
        /// <param name="memberMap"></param>
        private void getByIdentity<modelType>(CharStream sqlStream, string tableName, modelType value, MemberMap<modelType> memberMap)
            where modelType : class
        {
            sqlStream.WriteNotNull("select ");
            DataModel.Model<modelType>.GetNames(sqlStream, memberMap);
            sqlStream.WriteNotNull(" from `");
            sqlStream.WriteNotNull(tableName);
            sqlStream.WriteNotNull("` where ");
            sqlStream.WriteNotNull(DataModel.Model<modelType>.IdentitySqlName);
            sqlStream.Write('=');
            AutoCSer.Extension.Number.ToString(DataModel.Model<modelType>.GetIdentity(value), sqlStream);
            sqlStream.WriteNotNull(" limit 0,1;");
        }
        /// <summary>
        /// 查询对象
        /// </summary>
        /// <typeparam name="valueType">对象类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="value">匹配成员值</param>
        /// <param name="memberMap">成员位图</param>
        /// <param name="query">查询信息</param>
        internal override void GetByPrimaryKey<valueType, modelType>
            (Sql.Table<valueType, modelType> sqlTool, valueType value, MemberMap<modelType> memberMap, ref GetQuery<modelType> query)
        {
            query.MemberMap = DataModel.Model<modelType>.CopyMemberMap;
            if (memberMap != null && !memberMap.IsDefault) query.MemberMap.And(memberMap);
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                getByPrimaryKey(sqlStream, sqlTool.TableName, value, memberMap);
                query.Sql = sqlStream.ToString();
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
        }
        /// <summary>
        /// 查询对象
        /// </summary>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlStream"></param>
        /// <param name="tableName"></param>
        /// <param name="value"></param>
        /// <param name="memberMap"></param>
        private void getByPrimaryKey<modelType>(CharStream sqlStream, string tableName, modelType value, MemberMap<modelType> memberMap)
            where modelType : class
        {
            sqlStream.WriteNotNull("select ");
            DataModel.Model<modelType>.GetNames(sqlStream, memberMap);
            sqlStream.WriteNotNull(" from `");
            sqlStream.WriteNotNull(tableName);
            sqlStream.WriteNotNull("` where ");
            DataModel.Model<modelType>.PrimaryKeyWhere.Write(sqlStream, value, ConstantConverter.Default);
            sqlStream.WriteNotNull(" limit 0,1;");
        }
        /// <summary>
        /// 更新数据
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="value">匹配成员值</param>
        /// <param name="memberMap">成员位图</param>
        /// <param name="query">查询信息</param>
        /// <returns></returns>
        internal override bool Update<valueType, modelType>
            (Sql.Table<valueType, modelType> sqlTool, valueType value, MemberMap<modelType> memberMap, ref UpdateQuery<modelType> query)
        {
            if (query.MemberMap == null) query.MemberMap = sqlTool.GetSelectMemberMap(memberMap);
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                if (DataModel.Model<modelType>.Identity != null)
                {
                    getByIdentity(sqlStream, sqlTool.TableName, value, query.MemberMap);
                    query.Sql = sqlStream.ToString();
                    sqlStream.ByteSize = 0;
                    sqlStream.SimpleWriteNotNull(" update `");
                    sqlStream.SimpleWriteNotNull(sqlTool.TableName);
                    sqlStream.SimpleWriteNotNull("` set ");
                    DataModel.Model<modelType>.Updater.Update(sqlStream, memberMap, value, ConstantConverter.Default, sqlTool);
                    sqlStream.SimpleWriteNotNull(" where ");
                    sqlStream.SimpleWriteNotNull(DataModel.Model<modelType>.IdentitySqlName);
                    sqlStream.Write('=');
                    AutoCSer.Extension.Number.ToString(DataModel.Model<modelType>.GetIdentity(value), sqlStream);
                    sqlStream.Write(';');
                    query.UpdateSql = sqlStream.ToString();
                    return true;
                }
                if (DataModel.Model<modelType>.PrimaryKeys.Length != 0)
                {
                    getByPrimaryKey(sqlStream, sqlTool.TableName, value, query.MemberMap);
                    query.Sql = sqlStream.ToString();
                    sqlStream.ByteSize = 0;
                    sqlStream.WriteNotNull(" update `");
                    sqlStream.WriteNotNull(sqlTool.TableName);
                    sqlStream.WriteNotNull("` set ");
                    DataModel.Model<modelType>.Updater.Update(sqlStream, memberMap, value, ConstantConverter.Default, sqlTool);
                    sqlStream.WriteNotNull(" where ");
                    DataModel.Model<modelType>.PrimaryKeyWhere.Write(sqlStream, value, ConstantConverter.Default);
                    sqlStream.Write(';');
                    query.UpdateSql = sqlStream.ToString();
                    return true;
                }
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            return false;
        }
        /// <summary>
        /// 更新数据
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="connection">SQL连接</param>
        /// <param name="value">匹配成员值</param>
        /// <param name="memberMap">成员位图</param>
        /// <param name="query">查询信息</param>
        /// <returns>更新是否成功</returns>
        internal override bool Update<valueType, modelType>
            (Sql.Table<valueType, modelType> sqlTool, ref DbConnection connection, valueType value, MemberMap<modelType> memberMap, ref UpdateQuery<modelType> query)
        {
            GetQuery<modelType> getQuery = new GetQuery<modelType> { MemberMap = query.MemberMap, Sql = query.Sql };
            valueType oldValue = AutoCSer.Emit.Constructor<valueType>.New();
            if (Get(sqlTool, ref connection, oldValue, ref getQuery) && executeNonQuery(connection, query.UpdateSql) > 0 && Get(sqlTool, ref connection, value, ref getQuery))
            {
                sqlTool.CallOnUpdated(value, oldValue, memberMap);
                return true;
            }
            return false;
        }
        /// <summary>
        /// 获取添加数据 SQL 语句
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="value">添加数据</param>
        /// <param name="memberMap">成员位图</param>
        /// <param name="query">添加数据查询信息</param>
        internal override void insert<valueType, modelType>(Sql.Table<valueType, modelType> sqlTool, valueType value, MemberMap<modelType> memberMap, ref InsertQuery query)
        {
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                if (DataModel.Model<modelType>.Identity != null)
                {
                    long identity;
                    if (sqlTool.Attribute.IsSetIdentity) DataModel.Model<modelType>.SetIdentity(value, identity = sqlTool.NextIdentity);
                    else sqlTool.Identity64 = identity = DataModel.Model<modelType>.GetIdentity(value);
                    sqlStream.WriteNotNull("insert into`");
                    sqlStream.WriteNotNull(sqlTool.TableName);
                    sqlStream.WriteNotNull("`(");
                    DataModel.Model<modelType>.Inserter.GetColumnNames(sqlStream, memberMap);
                    sqlStream.WriteNotNull(")values(");
                    DataModel.Model<modelType>.Inserter.Insert(sqlStream, memberMap, value, ConstantConverter.Default, sqlTool);
                    sqlStream.WriteNotNull(");");
                    query.InsertSql = sqlStream.ToString();
                    sqlStream.ByteSize = 0;
                    getByIdentity(sqlStream, sqlTool.TableName, value, memberMap);
                    query.Sql = sqlStream.ToString();
                }
                else
                {
                    sqlStream.WriteNotNull("insert into`");
                    sqlStream.WriteNotNull(sqlTool.TableName);
                    sqlStream.WriteNotNull("`(");
                    DataModel.Model<modelType>.Inserter.GetColumnNames(sqlStream, memberMap);
                    sqlStream.WriteNotNull(")values(");
                    DataModel.Model<modelType>.Inserter.Insert(sqlStream, memberMap, value, ConstantConverter.Default, sqlTool);
                    sqlStream.WriteNotNull(");");
                    query.InsertSql = sqlStream.ToString();
                    if (DataModel.Model<modelType>.PrimaryKeys.Length != 0)
                    {
                        sqlStream.ByteSize = 0;
                        getByPrimaryKey(sqlStream, sqlTool.TableName, value, memberMap);
                        query.Sql = sqlStream.ToString();
                    }
                }
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
        }
        /// <summary>
        /// 添加数据
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="connection">SQL连接</param>
        /// <param name="value">添加数据</param>
        /// <param name="query">添加数据查询信息</param>
        /// <returns></returns>
        internal override bool Insert<valueType, modelType>(Sql.Table<valueType, modelType> sqlTool, ref DbConnection connection, valueType value, ref InsertQuery query)
        {
            if (executeNonQuery(ref connection, query.InsertSql) > 0)
            {
                if (DataModel.Model<modelType>.Identity != null || DataModel.Model<modelType>.PrimaryKeys.Length != 0)
                {
                    GetQuery<modelType> getQuery = new GetQuery<modelType> { MemberMap = DataModel.Model<modelType>.MemberMap, Sql = query.Sql };
                    if (!Get(sqlTool, ref connection, value, ref getQuery)) return false;
                }
                sqlTool.CallOnInserted(value);
                return true;
            }
            return false;
        }
        /// <summary>
        /// 获取删除数据 SQL 语句
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="value">删除数据</param>
        /// <param name="query">删除数据查询信息</param>
        /// <returns></returns>
        internal override bool Delete<valueType, modelType>(Sql.Table<valueType, modelType> sqlTool, valueType value, ref InsertQuery query)
        {
            CharStream sqlStream = Interlocked.Exchange(ref this.sqlStream, null);
            if (sqlStream == null) sqlStream = new CharStream(null, 0);
            byte* buffer = null;
            try
            {
                sqlStream.Reset(buffer = AutoCSer.UnmanagedPool.Default.Get(), AutoCSer.UnmanagedPool.DefaultSize);
                if (DataModel.Model<modelType>.Identity != null)
                {
                    sqlStream.WriteNotNull("delete from `");
                    sqlStream.WriteNotNull(sqlTool.TableName);
                    sqlStream.WriteNotNull("' where ");
                    sqlStream.WriteNotNull(DataModel.Model<modelType>.IdentitySqlName);
                    sqlStream.Write('=');
                    AutoCSer.Extension.Number.ToString(DataModel.Model<modelType>.GetIdentity(value), sqlStream);
                    sqlStream.Write(';');
                    query.InsertSql = sqlStream.ToString();
                    sqlStream.ByteSize = 0;
                    getByIdentity(sqlStream, sqlTool.TableName, value, sqlTool.SelectMemberMap);
                    query.Sql = sqlStream.ToString();
                    return true;
                }
                else if (DataModel.Model<modelType>.PrimaryKeys.Length != 0)
                {
                    sqlStream.WriteNotNull("delete from `");
                    sqlStream.WriteNotNull(sqlTool.TableName);
                    sqlStream.WriteNotNull("' where ");
                    DataModel.Model<modelType>.PrimaryKeyWhere.Write(sqlStream, value, ConstantConverter.Default);
                    sqlStream.Write(';');
                    query.InsertSql = sqlStream.ToString();
                    sqlStream.ByteSize = 0;
                    getByPrimaryKey(sqlStream, sqlTool.TableName, value, sqlTool.SelectMemberMap);
                    query.Sql = sqlStream.ToString();
                    return true;
                }
            }
            finally
            {
                if (buffer != null) AutoCSer.UnmanagedPool.Default.Push(buffer);
                sqlStream.Dispose();
                Interlocked.Exchange(ref this.sqlStream, sqlStream);
            }
            return false;
        }
        /// <summary>
        /// 删除数据
        /// </summary>
        /// <typeparam name="valueType">数据类型</typeparam>
        /// <typeparam name="modelType">模型类型</typeparam>
        /// <param name="sqlTool">SQL操作工具</param>
        /// <param name="connection">SQL连接</param>
        /// <param name="value">添加数据</param>
        /// <param name="query">添加数据查询信息</param>
        /// <returns></returns>
        internal override bool Delete<valueType, modelType>(Sql.Table<valueType, modelType> sqlTool, ref DbConnection connection, valueType value, ref InsertQuery query)
        {
            GetQuery<modelType> getQuery = new GetQuery<modelType> { MemberMap = sqlTool.SelectMemberMap, Sql = query.Sql };
            if (Get(sqlTool, ref connection, value, ref getQuery) && executeNonQuery(connection, query.InsertSql) > 0)
            {
                sqlTool.CallOnDeleted(value);
                return true;
            }
            return false;
        }
    }
}
