targetScope = 'resourceGroup'

@description('Environment name')
param environment string

@description('Location')
param location string = 'eastus'

@description('App name prefix')
param namePrefix string = 'legalrag'

var suffix = '${namePrefix}-${environment}'
