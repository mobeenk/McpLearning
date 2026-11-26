USE [Northwind];
GO
IF DATABASE_PRINCIPAL_ID(N'McpNorthwindReader') IS NOT NULL
	THROW 50000, 'McpNorthwindReader already exists. Review its permissions before using the database tools.', 1;
GO
CREATE USER [McpNorthwindReader] WITHOUT LOGIN;
GRANT CONNECT TO [McpNorthwindReader];
GRANT SELECT ON SCHEMA::[dbo] TO [McpNorthwindReader];
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, TAKE OWNERSHIP TO [McpNorthwindReader];
GO
