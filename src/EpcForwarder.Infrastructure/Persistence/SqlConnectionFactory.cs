// src/EpcForwarder.Infrastructure/Persistence/SqlConnectionFactory.cs
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlConnectionFactory(string connectionString)
{
    public SqlConnection Create() => new(connectionString);
}
